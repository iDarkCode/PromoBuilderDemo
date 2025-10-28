using System;

namespace PromoEngine.Domain
{
    // ========================================
    // DOMAIN EVENTS
    // ========================================

    /// <summary>
    /// Interfaz base para todos los eventos de dominio
    /// </summary>
    public interface IDomainEvent
    {
        /// <summary>
        /// Identificador único del evento
        /// </summary>
        Guid Id { get; }

        /// <summary>
        /// Fecha y hora cuando ocurrió el evento
        /// </summary>
        DateTimeOffset OccurredAt { get; }
    }

    /// <summary>
    /// Evento que se dispara cuando se crea una nueva promoción
    /// </summary>
    public sealed class PromotionCreatedEvent : IDomainEvent
    {
        public Guid Id { get; }
        public DateTimeOffset OccurredAt { get; }
        public Guid PromotionId { get; }
        public string PromotionName { get; }
        public string Timezone { get; }
        public int GlobalCooldownDays { get; }

        public PromotionCreatedEvent(Guid promotionId, string promotionName, string timezone, int globalCooldownDays)
        {
            Id = Guid.NewGuid();
            OccurredAt = DateTimeOffset.UtcNow;
            PromotionId = promotionId;
            PromotionName = promotionName;
            Timezone = timezone;
            GlobalCooldownDays = globalCooldownDays;
        }
    }

    /// <summary>
    /// Evento que se dispara cuando se publica una versión de promoción
    /// </summary>
    public sealed class PromotionVersionPublishedEvent : IDomainEvent
    {
        public Guid Id { get; }
        public DateTimeOffset OccurredAt { get; }
        public Guid PromotionId { get; }
        public Guid VersionId { get; }
        public int Version { get; }
        public string CountryIso { get; }

        public PromotionVersionPublishedEvent(Guid promotionId, Guid versionId, int version, string countryIso)
        {
            Id = Guid.NewGuid();
            OccurredAt = DateTimeOffset.UtcNow;
            PromotionId = promotionId;
            VersionId = versionId;
            Version = version;
            CountryIso = countryIso;
        }
    }

    /// <summary>
    /// Evento que se dispara cuando se otorga una recompensa a un contacto
    /// </summary>
    public sealed class RewardGrantedEvent : IDomainEvent
    {
        public Guid Id { get; }
        public DateTimeOffset OccurredAt { get; }
        public Guid ContactRewardId { get; }
        public Guid ContactId { get; }
        public Guid PromotionId { get; }
        public Guid? RewardId { get; }
        public int TierLevel { get; }
        public decimal GrantedAmount { get; }
        public string GrantedUnit { get; }
        public string? SourceEventId { get; }

        public RewardGrantedEvent(
            Guid contactRewardId,
            Guid contactId,
            Guid promotionId,
            Guid? rewardId,
            int tierLevel,
            decimal grantedAmount,
            string grantedUnit,
            string? sourceEventId)
        {
            Id = Guid.NewGuid();
            OccurredAt = DateTimeOffset.UtcNow;
            ContactRewardId = contactRewardId;
            ContactId = contactId;
            PromotionId = promotionId;
            RewardId = rewardId;
            TierLevel = tierLevel;
            GrantedAmount = grantedAmount;
            GrantedUnit = grantedUnit;
            SourceEventId = sourceEventId;
        }
    }

    /// <summary>
    /// Evento que se dispara cuando se rechaza el otorgamiento de una recompensa
    /// </summary>
    public sealed class RewardRejectedEvent : IDomainEvent
    {
        public Guid Id { get; }
        public DateTimeOffset OccurredAt { get; }
        public Guid ContactRewardId { get; }
        public Guid ContactId { get; }
        public Guid PromotionId { get; }
        public string RejectionReason { get; }

        public RewardRejectedEvent(Guid contactRewardId, Guid contactId, Guid promotionId, string rejectionReason)
        {
            Id = Guid.NewGuid();
            OccurredAt = DateTimeOffset.UtcNow;
            ContactRewardId = contactRewardId;
            ContactId = contactId;
            PromotionId = promotionId;
            RejectionReason = rejectionReason;
        }
    }

    /// <summary>
    /// Evento que se dispara cuando se crea una nueva recompensa
    /// </summary>
    public sealed class RewardCreatedEvent : IDomainEvent
    {
        public Guid Id { get; }
        public DateTimeOffset OccurredAt { get; }
        public Guid RewardId { get; }
        public string RewardName { get; }
        public RewardType RewardType { get; }
        public decimal Value { get; }
        public string Unit { get; }

        public RewardCreatedEvent(Guid rewardId, string rewardName, RewardType rewardType, decimal value, string unit)
        {
            Id = Guid.NewGuid();
            OccurredAt = DateTimeOffset.UtcNow;
            RewardId = rewardId;
            RewardName = rewardName;
            RewardType = rewardType;
            Value = value;
            Unit = unit;
        }
    }

    /// <summary>
    /// Evento que se dispara cuando se desactiva una recompensa
    /// </summary>
    public sealed class RewardDeactivatedEvent : IDomainEvent
    {
        public Guid Id { get; }
        public DateTimeOffset OccurredAt { get; }
        public Guid RewardId { get; }
        public string RewardName { get; }

        public RewardDeactivatedEvent(Guid rewardId, string rewardName)
        {
            Id = Guid.NewGuid();
            OccurredAt = DateTimeOffset.UtcNow;
            RewardId = rewardId;
            RewardName = rewardName;
        }
    }
}