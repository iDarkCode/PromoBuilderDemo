using FastEndpoints; using Microsoft.EntityFrameworkCore; using PromoEngine.Application; using PromoEngine.Domain; using PromoEngine.Infrastructure.Authoring; using PromoEngine.Infrastructure.EF; using System.Text.Json;
namespace PromoEngine.WebApi.Authoring{
 public sealed class UpsertPromotionDraftEndpoint:Endpoint<UpsertPromotionDraftRequest,UpsertPromotionDraftResponse>{ private readonly PromoEngineDbContext _db; private readonly IAttributesService _attrs; private readonly IPromotionCompiler _compiler;
  public UpsertPromotionDraftEndpoint(PromoEngineDbContext db,IAttributesService attrs,IPromotionCompiler compiler){_db=db;_attrs=attrs;_compiler=compiler;}
  public override void Configure(){ Post("/api/authoring/promotions/draft"); }
  public override async Task HandleAsync(UpsertPromotionDraftRequest req,CancellationToken ct){
    var attrs=await _attrs.GetAttributesAsync(ct); var ops=await _attrs.GetOperatorsAsync(ct); var sup=await _attrs.GetOperatorSupportedTypesAsync(ct);
    var shaped=CompilerUtils.ShapeCatalogs(attrs, ops, sup); var (wf,warnings)=_compiler.BuildWorkflow(req, shaped.attrs, shaped.ops, shaped.supported); var wfJson=JsonSerializer.Serialize(wf);
    var pid=req.PromotionId ?? Guid.NewGuid();
    var p=req.PromotionId is null ? Promotion.Create(pid, req.Name, req.Timezone, req.Policies.GlobalCooldownDays) : await _db.Promotions.FindAsync(new object?[]{pid}, ct) ?? Promotion.Create(pid, req.Name, req.Timezone, req.Policies.GlobalCooldownDays);
    var newVersion= (await _db.PromotionVersions.Where(x=>x.PromotionId==pid && x.CountryIso==req.CountryIso).MaxAsync(x=>(int?)x.Version, ct) ?? 0) + 1;
    var pv= PromotionVersion.Create(pid, newVersion, DateTimeOffset.UtcNow, JsonSerializer.Serialize(new{ policies=new{ globalCooldownDays=req.Policies.GlobalCooldownDays, exclusivePerEvent=req.Policies.ExclusivePerEvent, country=req.CountryIso }, window=new{ req.Window.ValidFromUtc, req.Window.ValidToUtc }, segments=req.Segments }), wfJson, req.Timezone, req.Policies.GlobalCooldownDays, req.CountryIso, req.Window.ValidFromUtc, req.Window.ValidToUtc);
    var tiers=new List<RuleTier>(); var groups=new List<RuleExpressionGroup>();
    foreach(var t in req.Tiers.OrderBy(x=>x.TierLevel).ThenBy(x=>x.Order)){ var tier=RuleTier.Create(pid, t.TierLevel, t.Order, t.CooldownDaysBetweenTiers); tiers.Add(tier); foreach(var g in t.Groups.OrderBy(x=>x.Order)){ var eg=RuleExpressionGroup.Create(pid, tier.Id, g.Order, JsonSerializer.Serialize(g.ExpressionRoot)); groups.Add(eg); foreach(var rid in g.RewardIds) _db.RuleGroupRewards.Add(new RuleGroupReward{ ExpressionGroupId=eg.Id, RewardId=rid}); } }
    foreach(var rid in req.GlobalRewardIds) _db.PromotionRewards.Add(new PromotionReward{ PromotionId=pid, RewardId=rid });
    _db.Promotions.Attach(p); _db.PromotionVersions.Add(pv); _db.RuleTiers.AddRange(tiers); _db.ExpressionGroups.AddRange(groups);
    await _db.SaveChangesAsync(ct);
    await SendOkAsync(new UpsertPromotionDraftResponse(pid, newVersion, req.CountryIso, wf.WorkflowName, warnings), ct);
  } }
 public sealed class PublishPromotionEndpoint:Endpoint<(Guid promotionId,string countryIso),object>{ private readonly PromoEngineDbContext _db; private readonly IPromotionCache _cache; private readonly IPromotionPublisher _publisher;
  public PublishPromotionEndpoint(PromoEngineDbContext db,IPromotionCache cache,IPromotionPublisher publisher){_db=db;_cache=cache;_publisher=publisher;}
  public override void Configure(){ Post("/api/authoring/promotions/{promotionId:guid}/{countryIso}/publish"); }
  public override async Task HandleAsync((Guid promotionId,string countryIso) req,CancellationToken ct){ var (promotionId,countryIso)=req; var p=await _db.Promotions.FindAsync(new object?[]{promotionId}, ct); if(p is null){ await SendNotFoundAsync(ct); return; }
   var pv=await _db.PromotionVersions.Where(x=>x.PromotionId==promotionId && x.CountryIso==countryIso).OrderByDescending(x=>x.Version).FirstOrDefaultAsync(ct); if(pv is null){ await SendNotFoundAsync(ct); return; }
   pv.IsDraft=false; await _db.SaveChangesAsync(ct); await _cache.WarmAsync(p, pv, ct); await _publisher.PublishAsync(promotionId, countryIso, pv.Version, ct); await SendOkAsync(new{promotionId,countryIso,version=pv.Version}, ct); } }
}