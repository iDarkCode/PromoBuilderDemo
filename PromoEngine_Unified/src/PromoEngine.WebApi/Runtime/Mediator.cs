using MediatR; using PromoEngine.Application; using PromoEngine.Domain; using RulesEngine.Models; using System.Text.Json; using System.Linq;
namespace PromoEngine.WebApi.Runtime{
 public sealed record EvaluatePromotionCommand(EvaluateRequest Request):IRequest<IReadOnlyList<EvaluateResult>>;
 public sealed class EvaluatePromotionHandler:IRequestHandler<EvaluatePromotionCommand,IReadOnlyList<EvaluateResult>>{
  private readonly IPromotionProvider _promotions; private readonly ISegmentService _segments; private readonly IRuleEngineService _engine; private readonly IRewardGrantService _grants; private readonly IContactRewardRepository _contactRewards; private readonly IRuleTierRepository _tiers; private readonly IExpressionGroupRepository _groups; private readonly IPromotionRewardRepository _rewards; private readonly IPromotionCache _cache;
  public EvaluatePromotionHandler(IPromotionProvider promotions,ISegmentService segments,IRuleEngineService engine,IRewardGrantService grants,IContactRewardRepository contactRewards,IRuleTierRepository tiers,IExpressionGroupRepository groups,IPromotionRewardRepository rewards,IPromotionCache cache){ _promotions=promotions; _segments=segments; _engine=engine; _grants=grants; _contactRewards=contactRewards; _tiers=tiers; _groups=groups; _rewards=rewards; _cache=cache; }
  public async Task<IReadOnlyList<EvaluateResult>> Handle(EvaluatePromotionCommand cmd,CancellationToken ct){
    var req=cmd.Request; var results=new List<EvaluateResult>(); var promos=await _promotions.GetActivePromotionsAsync(req.CountryIso, req.AsOfUtc, ct); var segs=await _segments.GetSegmentsForContactAsync(req.ContactId, req.CountryIso, ct);
    foreach(var (p,pv) in promos){
      if(!ContactInAnyRequiredSegment(pv.ManifestJson, segs)) continue;
      if(!string.IsNullOrWhiteSpace(req.Ctx.EventId) && await _contactRewards.ExistsForEventAsync(req.ContactId, p.Id, req.Ctx.EventId, ct)) continue;
      var last=await _contactRewards.GetLastGrantedAsync(p.Id, req.ContactId, ct);
      var canTier1= last is null || last.GrantedAt.AddDays(pv.GlobalCooldownDays) <= req.AsOfUtc;
      var wf=JsonSerializer.Deserialize<WorkflowRules>(pv.WorkflowJson)!;
      var tiers=await _tiers.GetTiersAsync(p.Id, ct);
      var exclusive= ReadExclusive(pv.ManifestJson) ?? true;
      foreach(var t in tiers){
        if(t.TierLevel==1 && !canTier1) continue;
        if(t.TierLevel>1){
          var prev=await _contactRewards.GetLastGrantedForTierAsync(p.Id, req.ContactId, t.TierLevel-1, ct);
          if(prev is null) continue;
          if(t.CooldownDays.HasValue && prev.GrantedAt.AddDays(t.CooldownDays.Value) > req.AsOfUtc) continue;
        }
        var groups=await _groups.GetGroupsAsync(t.Id, ct); bool awarded=false;
        foreach(var g in groups){
          var ruleName=$"tier:{t.TierLevel}:group:{g.Order}";
          var ctx=new { ctx = req.Ctx }; // simple params object
          if(!await _engine.EvaluateAsync(wf, ruleName, new RuntimeEventContext(req.Ctx, pv.Timezone), ct)) continue;
          var globalRewards=await _rewards.GetGlobalRewardsAsync(p.Id, ct);
          var groupRewards=await _rewards.GetGroupRewardsAsync(g.Id, ct);
          var allRewards= groupRewards.Count>0 ? groupRewards : globalRewards;
          await _grants.GrantAsync(req.ContactId, p, pv, t.TierLevel, g.Id, allRewards, req.Ctx, req.AsOfUtc, t.CooldownDays, ct);
          results.Add(new EvaluateResult(p.Id, pv.Version, pv.CountryIso, t.TierLevel, g.Id, allRewards));
          await _cache.WarmAsync(p, pv, ct);
          awarded=true; break;
        }
        if(awarded && exclusive) break;
      }
    }
    return results;
  }
  static bool ContactInAnyRequiredSegment(string manifestJson, IReadOnlyList<string> segs){ try{ using var doc=System.Text.Json.JsonDocument.Parse(manifestJson); if(doc.RootElement.TryGetProperty("segments", out var s) && s.ValueKind==System.Text.Json.JsonValueKind.Array){ var required=s.EnumerateArray().Select(x=>x.GetString()).Where(x=>x is not null)!.ToHashSet(); return required.Count==0 || required.Overlaps(segs); } }catch{} return true; }
  static bool? ReadExclusive(string manifestJson){ try{ using var doc=System.Text.Json.JsonDocument.Parse(manifestJson); if(doc.RootElement.TryGetProperty("policies", out var pol) && pol.TryGetProperty("exclusivePerEvent", out var ex)) return ex.GetBoolean(); } catch{} return null; }
  private sealed class RuntimeEventContext{ public double Gasto{get;} public string Club{get;}="" ; public bool EsVip{get;} public string EventId{get;}="";
    public RuntimeEventContext(EventContextDto dto,string tz){ Gasto=dto.Gasto; Club=dto.Club; EsVip=dto.EsVip; EventId=dto.EventId; } }
 } }