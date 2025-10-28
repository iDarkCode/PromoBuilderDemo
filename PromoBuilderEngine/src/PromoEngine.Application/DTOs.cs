using System; using System.Collections.Generic; using PromoEngine.Domain;
namespace PromoEngine.Application{
 public enum LogicKind{Group=1,Clause=2}
 public sealed record LogicNodeDto(LogicKind Kind,int? BoolOperator,IReadOnlyList<LogicNodeDto>? Children,Guid? AttributeId,Guid? OperatorId,string? ValueRaw,int? Order);
 public sealed record TierExpressionGroupDto(int Order, LogicNodeDto ExpressionRoot, IReadOnlyList<Guid> RewardIds);
 public sealed record TierDto(int TierLevel,int Order,int? CooldownDaysBetweenTiers,IReadOnlyList<TierExpressionGroupDto> Groups);
 public sealed record PromotionPoliciesDto(int GlobalCooldownDays,bool ExclusivePerEvent);
 public sealed record PromotionWindowDto(DateTimeOffset? ValidFromUtc, DateTimeOffset? ValidToUtc);
 public sealed record UpsertPromotionDraftRequest(Guid? PromotionId,string Name,string Timezone,string CountryIso,PromotionPoliciesDto Policies,PromotionWindowDto Window,IReadOnlyList<string> Segments,IReadOnlyList<Guid> GlobalRewardIds,IReadOnlyList<TierDto> Tiers,string? ClientRequestId);
 public sealed record UpsertPromotionDraftResponse(Guid PromotionId,int Version,string CountryIso,string WorkflowName,IReadOnlyList<string> Warnings);
 public sealed record EventContextDto(double Gasto,string Club,bool EsVip,string EventId);
 public sealed record EvaluateRequest(Guid ContactId,string CountryIso,DateTimeOffset AsOfUtc,EventContextDto Ctx);
 public sealed record EvaluateResult(Guid PromotionId,int Version,string CountryIso,int AwardedTier,Guid? ExpressionGroupId,IReadOnlyList<Guid> RewardIds);
}