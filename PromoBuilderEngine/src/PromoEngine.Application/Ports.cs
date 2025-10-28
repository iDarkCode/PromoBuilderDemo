using System; using System.Collections.Generic; using System.Threading; using System.Threading.Tasks; using PromoEngine.Domain; using RulesEngine.Models;
namespace PromoEngine.Application{
 public interface IPromotionProvider{ Task<IReadOnlyList<(Promotion p,PromotionVersion pv)>> GetActivePromotionsAsync(string countryIso,DateTimeOffset asOfUtc,CancellationToken ct); }
 public interface IRuleEngineService{ Task<bool> EvaluateAsync(WorkflowRules wf,string ruleName,object ctx,CancellationToken ct); }
 public interface IRewardGrantService{ Task GrantAsync(Guid contactId,Promotion p,PromotionVersion pv,int tierLevel,Guid? expressionGroupId,IReadOnlyList<Guid> rewardIds,EventContextDto ctxDto,DateTimeOffset asOfUtc,int? tierCooldownDays,CancellationToken ct); }
 public interface ISegmentService{ Task<IReadOnlyList<string>> GetSegmentsForContactAsync(Guid contactId,string country,CancellationToken ct); }
 public interface IAttributesService{ Task<IReadOnlyList<AttributeCatalog>> GetAttributesAsync(CancellationToken ct); Task<IReadOnlyList<OperatorCatalog>> GetOperatorsAsync(CancellationToken ct); Task<IReadOnlyList<OperatorSupportedType>> GetOperatorSupportedTypesAsync(CancellationToken ct); }
 public interface IPromotionCache{ Task WarmAsync(Promotion p,PromotionVersion pv,CancellationToken ct); }
 public interface IRuleTierRepository{ Task<IReadOnlyList<RuleTier>> GetTiersAsync(Guid promotionId,CancellationToken ct); }
 public interface IExpressionGroupRepository{ Task<IReadOnlyList<RuleExpressionGroup>> GetGroupsAsync(Guid tierId,CancellationToken ct); }
 public interface IPromotionRewardRepository{ Task<IReadOnlyList<Guid>> GetGlobalRewardsAsync(Guid promotionId,CancellationToken ct); Task<IReadOnlyList<Guid>> GetGroupRewardsAsync(Guid expressionGroupId,CancellationToken ct); }
 public interface IContactRewardRepository{ Task<ContactReward?> GetLastGrantedAsync(Guid promotionId,Guid contactId,CancellationToken ct); Task<ContactReward?> GetLastGrantedForTierAsync(Guid promotionId,Guid contactId,int tierLevel,CancellationToken ct); Task<bool> ExistsForEventAsync(Guid contactId,Guid promotionId,string eventId,CancellationToken ct); }
 public interface IPromotionCompiler{ (WorkflowRules Workflow, List<string> Warnings) BuildWorkflow(UpsertPromotionDraftRequest req,IReadOnlyDictionary<Guid,AttributeCatalog> attrs,IReadOnlyDictionary<Guid,OperatorCatalog> ops,IReadOnlySet<(Guid operatorId, DataType type)> supported); }
 public interface IPromotionPublisher{ Task PublishAsync(Guid promotionId,string countryIso,int version,CancellationToken ct); }
}