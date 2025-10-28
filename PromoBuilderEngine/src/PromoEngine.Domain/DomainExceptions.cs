using System;

namespace PromoEngine.Domain
{
    // ========================================
    // DOMAIN EXCEPTIONS
    // ========================================

    /// <summary>
    /// Excepción base para todas las excepciones de dominio del motor de promociones
    /// </summary>
    public abstract class PromoEngineDomainException : Exception
    {
        protected PromoEngineDomainException(string message) : base(message) { }
        protected PromoEngineDomainException(string message, Exception innerException) : base(message, innerException) { }
    }

    /// <summary>
    /// Excepción lanzada cuando se intenta crear una promoción con datos inválidos
    /// </summary>
    public sealed class InvalidPromotionException : PromoEngineDomainException
    {
        public Guid? PromotionId { get; }

        public InvalidPromotionException(string message, Guid? promotionId = null)
            : base(message)
        {
            PromotionId = promotionId;
        }
    }

    /// <summary>
    /// Excepción lanzada cuando se intenta realizar una operación en una promoción que no existe
    /// </summary>
    public sealed class PromotionNotFoundException : PromoEngineDomainException
    {
        public Guid PromotionId { get; }

        public PromotionNotFoundException(Guid promotionId)
            : base($"No se encontró la promoción con ID: {promotionId}")
        {
            PromotionId = promotionId;
        }
    }

    /// <summary>
    /// Excepción lanzada cuando se intenta crear una versión duplicada de una promoción
    /// </summary>
    public sealed class DuplicatePromotionVersionException : PromoEngineDomainException
    {
        public Guid PromotionId { get; }
        public int Version { get; }
        public string CountryIso { get; }

        public DuplicatePromotionVersionException(Guid promotionId, int version, string countryIso)
            : base($"Ya existe la versión {version} de la promoción {promotionId} para el país {countryIso}")
        {
            PromotionId = promotionId;
            Version = version;
            CountryIso = countryIso;
        }
    }

    /// <summary>
    /// Excepción lanzada cuando se intenta publicar una versión que ya está publicada
    /// </summary>
    public sealed class PromotionVersionAlreadyPublishedException : PromoEngineDomainException
    {
        public Guid VersionId { get; }
        public int Version { get; }

        public PromotionVersionAlreadyPublishedException(Guid versionId, int version)
            : base($"La versión {version} con ID {versionId} ya está publicada")
        {
            VersionId = versionId;
            Version = version;
        }
    }

    /// <summary>
    /// Excepción lanzada cuando se intenta crear una recompensa con datos inválidos
    /// </summary>
    public sealed class InvalidRewardException : PromoEngineDomainException
    {
        public Guid? RewardId { get; }

        public InvalidRewardException(string message, Guid? rewardId = null)
            : base(message)
        {
            RewardId = rewardId;
        }
    }

    /// <summary>
    /// Excepción lanzada cuando se intenta realizar una operación en una recompensa que no existe
    /// </summary>
    public sealed class RewardNotFoundException : PromoEngineDomainException
    {
        public Guid RewardId { get; }

        public RewardNotFoundException(Guid rewardId)
            : base($"No se encontró la recompensa con ID: {rewardId}")
        {
            RewardId = rewardId;
        }
    }

    /// <summary>
    /// Excepción lanzada cuando se intenta otorgar una recompensa a un contacto en cooldown
    /// </summary>
    public sealed class ContactInCooldownException : PromoEngineDomainException
    {
        public Guid ContactId { get; }
        public Guid PromotionId { get; }
        public DateTimeOffset CooldownUntil { get; }

        public ContactInCooldownException(Guid contactId, Guid promotionId, DateTimeOffset cooldownUntil)
            : base($"El contacto {contactId} está en cooldown para la promoción {promotionId} hasta {cooldownUntil}")
        {
            ContactId = contactId;
            PromotionId = promotionId;
            CooldownUntil = cooldownUntil;
        }
    }

    /// <summary>
    /// Excepción lanzada cuando se intenta cambiar el estado de un otorgamiento de recompensa de forma inválida
    /// </summary>
    public sealed class InvalidRewardStatusTransitionException : PromoEngineDomainException
    {
        public Guid ContactRewardId { get; }
        public RewardGrantStatus CurrentStatus { get; }
        public RewardGrantStatus AttemptedStatus { get; }

        public InvalidRewardStatusTransitionException(
            Guid contactRewardId,
            RewardGrantStatus currentStatus,
            RewardGrantStatus attemptedStatus)
            : base($"No se puede cambiar el estado de la recompensa {contactRewardId} de {currentStatus} a {attemptedStatus}")
        {
            ContactRewardId = contactRewardId;
            CurrentStatus = currentStatus;
            AttemptedStatus = attemptedStatus;
        }
    }

    /// <summary>
    /// Excepción lanzada cuando se intenta crear un tier de reglas duplicado
    /// </summary>
    public sealed class DuplicateRuleTierException : PromoEngineDomainException
    {
        public Guid PromotionId { get; }
        public int TierLevel { get; }

        public DuplicateRuleTierException(Guid promotionId, int tierLevel)
            : base($"Ya existe un tier con nivel {tierLevel} en la promoción {promotionId}")
        {
            PromotionId = promotionId;
            TierLevel = tierLevel;
        }
    }

    /// <summary>
    /// Excepción lanzada cuando se intenta crear un grupo de expresiones con orden duplicado
    /// </summary>
    public sealed class DuplicateExpressionGroupOrderException : PromoEngineDomainException
    {
        public Guid TierId { get; }
        public int Order { get; }

        public DuplicateExpressionGroupOrderException(Guid tierId, int order)
            : base($"Ya existe un grupo de expresiones con orden {order} en el tier {tierId}")
        {
            TierId = tierId;
            Order = order;
        }
    }

    /// <summary>
    /// Excepción lanzada cuando se intenta usar un período de validez inválido
    /// </summary>
    public sealed class InvalidValidityPeriodException : PromoEngineDomainException
    {
        public DateTimeOffset? ValidFrom { get; }
        public DateTimeOffset? ValidTo { get; }

        public InvalidValidityPeriodException(DateTimeOffset? validFrom, DateTimeOffset? validTo)
            : base($"Período de validez inválido: desde {validFrom} hasta {validTo}")
        {
            ValidFrom = validFrom;
            ValidTo = validTo;
        }
    }

    /// <summary>
    /// Excepción lanzada cuando se intenta crear un valor monetario con datos inválidos
    /// </summary>
    public sealed class InvalidMonetaryValueException : PromoEngineDomainException
    {
        public decimal? Amount { get; }
        public string? Unit { get; }

        public InvalidMonetaryValueException(string message, decimal? amount = null, string? unit = null)
            : base(message)
        {
            Amount = amount;
            Unit = unit;
        }
    }

    /// <summary>
    /// Excepción lanzada cuando se intenta crear un operador duplicado en el catálogo
    /// </summary>
    public sealed class DuplicateOperatorException : PromoEngineDomainException
    {
        public string OperatorCode { get; }

        public DuplicateOperatorException(string operatorCode)
            : base($"Ya existe un operador con el código: {operatorCode}")
        {
            OperatorCode = operatorCode;
        }
    }

    /// <summary>
    /// Excepción lanzada cuando se intenta crear un atributo duplicado en el catálogo
    /// </summary>
    public sealed class DuplicateAttributeException : PromoEngineDomainException
    {
        public string EntityLogicalName { get; }
        public string AttributeLogicalName { get; }

        public DuplicateAttributeException(string entityLogicalName, string attributeLogicalName)
            : base($"Ya existe el atributo {attributeLogicalName} en la entidad {entityLogicalName}")
        {
            EntityLogicalName = entityLogicalName;
            AttributeLogicalName = attributeLogicalName;
        }
    }

    /// <summary>
    /// Excepción lanzada cuando un operador no soporta un tipo de datos específico
    /// </summary>
    public sealed class UnsupportedDataTypeException : PromoEngineDomainException
    {
        public string OperatorCode { get; }
        public DataType DataType { get; }

        public UnsupportedDataTypeException(string operatorCode, DataType dataType)
            : base($"El operador {operatorCode} no soporta el tipo de datos {dataType}")
        {
            OperatorCode = operatorCode;
            DataType = dataType;
        }
    }
}