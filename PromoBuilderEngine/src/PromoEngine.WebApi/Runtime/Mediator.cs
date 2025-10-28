using MediatR;
using PromoEngine.Application;
using PromoEngine.Domain;
using RulesEngine.Models;
using System.Text.Json;
using Workflow = RulesEngine.Models.Workflow;

namespace PromoEngine.WebApi.Runtime
{
    /// <summary>
    /// Command for evaluating promotions against a specific request context.
    /// </summary>
    /// <param name="Request">The evaluation request containing contact and context information</param>
    public sealed record EvaluatePromotionCommand(EvaluateRequest Request) : IRequest<IReadOnlyList<EvaluateResult>>;

    /// <summary>
    /// Handles the evaluation of promotions by processing rules, segments, and rewards.
    /// Implements the MediatR pattern for decoupled command handling.
    /// </summary>
    public sealed class EvaluatePromotionHandler : IRequestHandler<EvaluatePromotionCommand, IReadOnlyList<EvaluateResult>>
    {
        #region Dependencies

        private readonly IPromotionProvider _promotionProvider;
        private readonly ISegmentService _segmentService;
        private readonly IRuleEngineService _ruleEngineService;
        private readonly IRewardGrantService _rewardGrantService;
        private readonly IContactRewardRepository _contactRewardRepository;
        private readonly IRuleTierRepository _ruleTierRepository;
        private readonly IExpressionGroupRepository _expressionGroupRepository;
        private readonly IPromotionRewardRepository _promotionRewardRepository;
        private readonly IPromotionCache _promotionCache;

        #endregion

        /// <summary>
        /// Initializes a new instance of the EvaluatePromotionHandler with required dependencies.
        /// </summary>
        public EvaluatePromotionHandler(
            IPromotionProvider promotionProvider,
            ISegmentService segmentService,
            IRuleEngineService ruleEngineService,
            IRewardGrantService rewardGrantService,
            IContactRewardRepository contactRewardRepository,
            IRuleTierRepository ruleTierRepository,
            IExpressionGroupRepository expressionGroupRepository,
            IPromotionRewardRepository promotionRewardRepository,
            IPromotionCache promotionCache)
        {
            _promotionProvider = promotionProvider ?? throw new ArgumentNullException(nameof(promotionProvider));
            _segmentService = segmentService ?? throw new ArgumentNullException(nameof(segmentService));
            _ruleEngineService = ruleEngineService ?? throw new ArgumentNullException(nameof(ruleEngineService));
            _rewardGrantService = rewardGrantService ?? throw new ArgumentNullException(nameof(rewardGrantService));
            _contactRewardRepository = contactRewardRepository ?? throw new ArgumentNullException(nameof(contactRewardRepository));
            _ruleTierRepository = ruleTierRepository ?? throw new ArgumentNullException(nameof(ruleTierRepository));
            _expressionGroupRepository = expressionGroupRepository ?? throw new ArgumentNullException(nameof(expressionGroupRepository));
            _promotionRewardRepository = promotionRewardRepository ?? throw new ArgumentNullException(nameof(promotionRewardRepository));
            _promotionCache = promotionCache ?? throw new ArgumentNullException(nameof(promotionCache));
        }
        /// <summary>
        /// Handles the promotion evaluation request by processing active promotions,
        /// checking segment membership, evaluating rules, and granting rewards.
        /// </summary>
        public async Task<IReadOnlyList<EvaluateResult>> Handle(EvaluatePromotionCommand command, CancellationToken cancellationToken)
        {
            var request = command.Request;
            var results = new List<EvaluateResult>();
            
            // Get active promotions and contact segments
            var promotions = await _promotionProvider.GetActivePromotionsAsync(request.CountryIso, request.AsOfUtc, cancellationToken);
            var contactSegments = await _segmentService.GetSegmentsForContactAsync(request.ContactId, request.CountryIso, cancellationToken);
            // Process each active promotion
            foreach (var (promotion, promotionVersion) in promotions)
            {
                // Check if contact is in required segments
                if (!ContactInAnyRequiredSegment(promotionVersion.ManifestJson, contactSegments))
                    continue;

                // Skip if already granted for this event
                if (!string.IsNullOrWhiteSpace(request.Ctx.EventId) &&
                    await _contactRewardRepository.ExistsForEventAsync(request.ContactId, promotion.Id, request.Ctx.EventId, cancellationToken))
                    continue;

                // Check global cooldown for tier 1
                var lastGranted = await _contactRewardRepository.GetLastGrantedAsync(promotion.Id, request.ContactId, cancellationToken);
                var canGrantTier1 = lastGranted is null || 
                    lastGranted.GrantedAt.AddDays(promotionVersion.GlobalCooldownDays) <= request.AsOfUtc;

                // Deserialize workflow rules
                var workflow = JsonSerializer.Deserialize<Workflow>(promotionVersion.WorkflowJson)!;
                var tiers = await _ruleTierRepository.GetTiersAsync(promotion.Id, cancellationToken);
                var isExclusive = ReadExclusive(promotionVersion.ManifestJson) ?? true;

                // Process each tier
                foreach (var tier in tiers)
                {
                    if (!await CanProcessTierAsync(tier, canGrantTier1, promotion, request, cancellationToken))
                        continue;

                    var (wasAwarded, shouldBreak) = await ProcessTierAsync(
                        tier, promotion, promotionVersion, workflow, request, results, cancellationToken);

                    if (wasAwarded && isExclusive && shouldBreak)
                        break;
                }
            }

            return results;
        }

        #region Private Helper Methods

        /// <summary>
        /// Determines if a tier can be processed based on cooldown and previous tier requirements.
        /// </summary>
        private async Task<bool> CanProcessTierAsync(
            RuleTier tier, 
            bool canGrantTier1, 
            Promotion promotion, 
            EvaluateRequest request, 
            CancellationToken cancellationToken)
        {
            // Check tier 1 cooldown
            if (tier.TierLevel == 1 && !canGrantTier1)
                return false;

            // Check previous tier requirements for higher tiers
            if (tier.TierLevel > 1)
            {
                var previousTierReward = await _contactRewardRepository.GetLastGrantedForTierAsync(
                    promotion.Id, request.ContactId, tier.TierLevel - 1, cancellationToken);
                
                if (previousTierReward is null)
                    return false;

                // Check tier-specific cooldown
                if (tier.CooldownDays.HasValue && 
                    previousTierReward.GrantedAt.AddDays(tier.CooldownDays.Value) > request.AsOfUtc)
                    return false;
            }

            return true;
        }

        /// <summary>
        /// Processes a single tier by evaluating expression groups and granting rewards.
        /// </summary>
        private async Task<(bool wasAwarded, bool shouldBreak)> ProcessTierAsync(
            RuleTier tier,
            Promotion promotion,
            PromotionVersion promotionVersion,
            Workflow workflow,
            EvaluateRequest request,
            List<EvaluateResult> results,
            CancellationToken cancellationToken)
        {
            var expressionGroups = await _expressionGroupRepository.GetGroupsAsync(tier.Id, cancellationToken);
            bool wasAwarded = false;

            foreach (var group in expressionGroups)
            {
                var ruleName = $"tier:{tier.TierLevel}:group:{group.Order}";
                var runtimeContext = new RuntimeEventContext(request.Ctx, promotionVersion.Timezone);

                // Evaluate rule engine
                if (!await _ruleEngineService.EvaluateAsync(workflow, ruleName, runtimeContext, cancellationToken))
                    continue;

                // Get rewards for this group
                var rewards = await GetRewardsForGroupAsync(promotion.Id, group.Id, cancellationToken);
                
                // Grant the reward
                await _rewardGrantService.GrantAsync(
                    request.ContactId, promotion, promotionVersion, tier.TierLevel, 
                    group.Id, rewards, request.Ctx, request.AsOfUtc, 
                    tier.CooldownDays, cancellationToken);

                // Add result
                results.Add(new EvaluateResult(
                    promotion.Id, promotionVersion.Version, promotionVersion.CountryIso, 
                    tier.TierLevel, group.Id, rewards));

                // Warm cache
                await _promotionCache.WarmAsync(promotion, promotionVersion, cancellationToken);

                wasAwarded = true;
                break; // Only process first matching group per tier
            }

            return (wasAwarded, true);
        }

        /// <summary>
        /// Gets rewards for a specific group, falling back to global rewards if no group-specific rewards exist.
        /// </summary>
        private async Task<IReadOnlyList<Guid>> GetRewardsForGroupAsync(
            Guid promotionId, 
            Guid groupId, 
            CancellationToken cancellationToken)
        {
            var groupRewards = await _promotionRewardRepository.GetGroupRewardsAsync(groupId, cancellationToken);
            
            if (groupRewards.Count > 0)
                return groupRewards;

            return await _promotionRewardRepository.GetGlobalRewardsAsync(promotionId, cancellationToken);
        }

        #endregion

        #region Static Helper Methods

        /// <summary>
        /// Determines if a contact is in any of the required segments for a promotion.
        /// </summary>
        /// <param name="manifestJson">The promotion manifest JSON containing segment requirements</param>
        /// <param name="contactSegments">The segments the contact belongs to</param>
        /// <returns>True if contact is in required segments or no segments are required</returns>
        private static bool ContactInAnyRequiredSegment(string manifestJson, IReadOnlyList<string> contactSegments)
        {
            try
            {
                using var document = JsonDocument.Parse(manifestJson);
                
                if (document.RootElement.TryGetProperty("segments", out var segmentsProperty) && 
                    segmentsProperty.ValueKind == JsonValueKind.Array)
                {
                    var requiredSegments = segmentsProperty
                        .EnumerateArray()
                        .Select(element => element.GetString())
                        .Where(segment => segment is not null)
                        .ToHashSet()!;

                    // If no segments required or contact is in any required segment
                    return requiredSegments.Count == 0 || requiredSegments.Overlaps(contactSegments);
                }
            }
            catch (JsonException)
            {
                // If JSON parsing fails, allow the promotion to proceed
            }

            return true;
        }

        /// <summary>
        /// Reads the exclusive policy from the promotion manifest.
        /// </summary>
        /// <param name="manifestJson">The promotion manifest JSON</param>
        /// <returns>The exclusive policy value, or null if not specified</returns>
        private static bool? ReadExclusive(string manifestJson)
        {
            try
            {
                using var document = JsonDocument.Parse(manifestJson);
                
                if (document.RootElement.TryGetProperty("policies", out var policies) && 
                    policies.TryGetProperty("exclusivePerEvent", out var exclusive))
                {
                    return exclusive.GetBoolean();
                }
            }
            catch (JsonException)
            {
                // If JSON parsing fails, return null
            }

            return null;
        }

        #endregion

        /// <summary>
        /// Runtime context for rule evaluation, containing event-specific data.
        /// </summary>
        private sealed class RuntimeEventContext
        {
            #region Properties

            /// <summary>
            /// The spending amount for the current context.
            /// </summary>
            public double Gasto { get; }

            /// <summary>
            /// The club membership identifier.
            /// </summary>
            public string Club { get; } = string.Empty;

            /// <summary>
            /// Indicates if the contact has VIP status.
            /// </summary>
            public bool EsVip { get; }

            /// <summary>
            /// The unique identifier for the current event.
            /// </summary>
            public string EventId { get; } = string.Empty;

            #endregion

            /// <summary>
            /// Initializes a new instance of the RuntimeEventContext.
            /// </summary>
            /// <param name="eventContext">The event context DTO containing the source data</param>
            /// <param name="timezone">The timezone for the promotion (currently unused)</param>
            public RuntimeEventContext(EventContextDto eventContext, string timezone)
            {
                Gasto = eventContext.Gasto;
                Club = eventContext.Club;
                EsVip = eventContext.EsVip;
                EventId = eventContext.EventId;
            }
        }
    }
}