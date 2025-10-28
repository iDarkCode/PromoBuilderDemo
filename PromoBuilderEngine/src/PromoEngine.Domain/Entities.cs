using System;
using System.Collections.Generic;
using System.Linq;

namespace PromoEngine.Domain
{
    // ========================================
    // ENUMERATIONS - Domain Value Objects
    // ========================================

    /// <summary>
    /// Representa los operadores booleanos disponibles para las reglas de negocio
    /// </summary>
    public enum BoolOperator
    {
        And = 1,
        Or = 2
    }

    /// <summary>
    /// Representa los tipos de datos soportados por el motor de reglas
    /// </summary>
    public enum DataType
    {
        String,
        Number,
        Date,
        Bool,
        Guid,
        StringArray,
        NumberArray
    }

    /// <summary>
    /// Representa los tipos de recompensas disponibles en el sistema
    /// </summary>
    public enum RewardType
    {
        Coupon = 1,
        Points = 2,
        Gift = 3,
        Cashback = 4
    }

    /// <summary>
    /// Estados posibles para el otorgamiento de recompensas
    /// </summary>
    public enum RewardGrantStatus
    {
        Granted = 1,
        Rejected = 2,
        Pending = 3
    }

    // ========================================
    // VALUE OBJECTS
    // ========================================

    /// <summary>
    /// Value Object que representa un período de validez para promociones
    /// </summary>
    public sealed class ValidityPeriod
    {
        public DateTimeOffset? ValidFromUtc { get; private set; }
        public DateTimeOffset? ValidToUtc { get; private set; }

        private ValidityPeriod() { }

        /// <summary>
        /// Crea un nuevo período de validez
        /// </summary>
        /// <param name="validFromUtc">Fecha de inicio (opcional)</param>
        /// <param name="validToUtc">Fecha de fin (opcional)</param>
        /// <returns>Nuevo período de validez</returns>
        /// <exception cref="ArgumentException">Cuando la fecha de fin es anterior a la de inicio</exception>
        public static ValidityPeriod Create(DateTimeOffset? validFromUtc, DateTimeOffset? validToUtc)
        {
            if (validFromUtc.HasValue && validToUtc.HasValue && validFromUtc > validToUtc)
                throw new ArgumentException("La fecha de inicio no puede ser posterior a la fecha de fin");

            return new ValidityPeriod
            {
                ValidFromUtc = validFromUtc,
                ValidToUtc = validToUtc
            };
        }

        /// <summary>
        /// Verifica si el período está activo en una fecha determinada
        /// </summary>
        public bool IsActiveAt(DateTimeOffset date)
        {
            if (ValidFromUtc.HasValue && date < ValidFromUtc.Value)
                return false;

            if (ValidToUtc.HasValue && date > ValidToUtc.Value)
                return false;

            return true;
        }
    }

    /// <summary>
    /// Value Object que representa un valor monetario con su unidad
    /// </summary>
    public sealed class MonetaryValue
    {
        public decimal Amount { get; private set; }
        public string Unit { get; private set; }

        private MonetaryValue() { Unit = default!; }

        /// <summary>
        /// Crea un nuevo valor monetario
        /// </summary>
        /// <param name="amount">Cantidad</param>
        /// <param name="unit">Unidad monetaria</param>
        /// <returns>Nuevo valor monetario</returns>
        /// <exception cref="ArgumentException">Cuando la cantidad es negativa o la unidad está vacía</exception>
        public static MonetaryValue Create(decimal amount, string unit)
        {
            if (amount < 0)
                throw new ArgumentException("El valor no puede ser negativo");

            if (string.IsNullOrWhiteSpace(unit))
                throw new ArgumentException("La unidad no puede estar vacía");

            return new MonetaryValue { Amount = amount, Unit = unit.Trim() };
        }
    }

    // ========================================
    // AGGREGATE ROOT: PROMOTION
    // ========================================

    /// <summary>
    /// Agregado raíz que representa una promoción en el sistema.
    /// Una promoción puede tener múltiples versiones y define las reglas de negocio básicas.
    /// </summary>
    public sealed class Promotion
    {
        private readonly List<PromotionVersion> _versions = new();

        /// <summary>
        /// Identificador único de la promoción
        /// </summary>
        public Guid Id { get; private set; }

        /// <summary>
        /// Nombre descriptivo de la promoción
        /// </summary>
        public string Name { get; private set; } = default!;

        /// <summary>
        /// Zona horaria base para la promoción
        /// </summary>
        public string Timezone { get; private set; } = "Europe/Madrid";

        /// <summary>
        /// Días de cooldown global entre otorgamientos de recompensas
        /// </summary>
        public int GlobalCooldownDays { get; private set; }

        /// <summary>
        /// Fecha de creación de la promoción
        /// </summary>
        public DateTimeOffset CreatedAt { get; private set; }

        /// <summary>
        /// Versiones de la promoción (solo lectura)
        /// </summary>
        public IReadOnlyList<PromotionVersion> Versions => _versions.AsReadOnly();

        // Constructor privado para EF Core
        private Promotion() { }

        /// <summary>
        /// Crea una nueva promoción
        /// </summary>
        /// <param name="id">Identificador único</param>
        /// <param name="name">Nombre de la promoción</param>
        /// <param name="timezone">Zona horaria</param>
        /// <param name="globalCooldownDays">Días de cooldown global</param>
        /// <returns>Nueva instancia de promoción</returns>
        /// <exception cref="ArgumentException">Cuando los parámetros son inválidos</exception>
        public static Promotion Create(Guid id, string name, string timezone, int globalCooldownDays)
        {
            if (id == Guid.Empty)
                throw new ArgumentException("El ID no puede ser vacío");

            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("El nombre no puede estar vacío");

            if (globalCooldownDays < 0)
                throw new ArgumentException("Los días de cooldown no pueden ser negativos");

            return new Promotion
            {
                Id = id,
                Name = name.Trim(),
                Timezone = string.IsNullOrWhiteSpace(timezone) ? "Europe/Madrid" : timezone.Trim(),
                GlobalCooldownDays = globalCooldownDays,
                CreatedAt = DateTimeOffset.UtcNow
            };
        }

        /// <summary>
        /// Añade una nueva versión a la promoción
        /// </summary>
        /// <param name="version">Versión a añadir</param>
        /// <exception cref="ArgumentException">Cuando la versión ya existe</exception>
        public void AddVersion(PromotionVersion version)
        {
            if (_versions.Any(v => v.Version == version.Version && v.CountryIso == version.CountryIso))
                throw new ArgumentException($"Ya existe la versión {version.Version} para el país {version.CountryIso}");

            _versions.Add(version);
        }
    }

    // ========================================
    // ENTITY: PROMOTION VERSION
    // ========================================

    /// <summary>
    /// Entidad que representa una versión específica de una promoción para un país determinado.
    /// Contiene las reglas y configuración específicas de la promoción.
    /// </summary>
    public sealed class PromotionVersion
    {
        private readonly List<RuleTier> _ruleTiers = new();

        /// <summary>
        /// Identificador único de la versión
        /// </summary>
        public Guid Id { get; private set; }

        /// <summary>
        /// ID de la promoción padre
        /// </summary>
        public Guid PromotionId { get; private set; }

        /// <summary>
        /// Número de versión
        /// </summary>
        public int Version { get; private set; }

        /// <summary>
        /// Código ISO del país
        /// </summary>
        public string CountryIso { get; private set; } = default!;

        /// <summary>
        /// Indica si la versión está en borrador
        /// </summary>
        public bool IsDraft { get; private set; }

        /// <summary>
        /// Configuración del flujo de trabajo en formato JSON
        /// </summary>
        public string WorkflowJson { get; private set; } = "{}";

        /// <summary>
        /// Manifiesto de la promoción en formato JSON
        /// </summary>
        public string ManifestJson { get; private set; } = "{}";

        /// <summary>
        /// Zona horaria específica de esta versión
        /// </summary>
        public string Timezone { get; private set; } = "Europe/Madrid";

        /// <summary>
        /// Días de cooldown global para esta versión
        /// </summary>
        public int GlobalCooldownDays { get; private set; }

        /// <summary>
        /// Período de validez de la versión
        /// </summary>
        public ValidityPeriod ValidityPeriod { get; private set; } = default!;

        /// <summary>
        /// Fecha de creación de la versión
        /// </summary>
        public DateTimeOffset CreatedAt { get; private set; }

        /// <summary>
        /// Niveles de reglas asociados (solo lectura)
        /// </summary>
        public IReadOnlyList<RuleTier> RuleTiers => _ruleTiers.AsReadOnly();

        // Constructor privado para EF Core
        private PromotionVersion() { }

        /// <summary>
        /// Crea una nueva versión de promoción
        /// </summary>
        public static PromotionVersion Create(
            Guid promotionId,
            int version,
            string countryIso,
            string manifestJson,
            string workflowJson,
            string timezone,
            int globalCooldownDays,
            DateTimeOffset? validFromUtc,
            DateTimeOffset? validToUtc)
        {
            if (promotionId == Guid.Empty)
                throw new ArgumentException("El ID de promoción no puede ser vacío");

            if (version < 1)
                throw new ArgumentException("La versión debe ser mayor a 0");

            if (string.IsNullOrWhiteSpace(countryIso))
                throw new ArgumentException("El código de país no puede estar vacío");

            return new PromotionVersion
            {
                Id = Guid.NewGuid(),
                PromotionId = promotionId,
                Version = version,
                CountryIso = countryIso.Trim().ToUpperInvariant(),
                IsDraft = true,
                WorkflowJson = workflowJson ?? "{}",
                ManifestJson = manifestJson ?? "{}",
                Timezone = string.IsNullOrWhiteSpace(timezone) ? "Europe/Madrid" : timezone.Trim(),
                GlobalCooldownDays = Math.Max(0, globalCooldownDays),
                ValidityPeriod = ValidityPeriod.Create(validFromUtc, validToUtc),
                CreatedAt = DateTimeOffset.UtcNow
            };
        }

        /// <summary>
        /// Publica la versión (cambia de borrador a publicada)
        /// </summary>
        /// <exception cref="InvalidOperationException">Cuando la versión ya está publicada</exception>
        public void Publish()
        {
            if (!IsDraft)
                throw new InvalidOperationException("La versión ya está publicada");

            IsDraft = false;
        }

        /// <summary>
        /// Añade un nivel de reglas a la versión
        /// </summary>
        public void AddRuleTier(RuleTier ruleTier)
        {
            if (_ruleTiers.Any(rt => rt.TierLevel == ruleTier.TierLevel))
                throw new ArgumentException($"Ya existe un tier con el nivel {ruleTier.TierLevel}");

            _ruleTiers.Add(ruleTier);
        }
    }

    // ========================================
    // ENTITY: RULE TIER
    // ========================================

    /// <summary>
    /// Entidad que representa un nivel de reglas dentro de una promoción.
    /// Los tiers permiten organizar las reglas en niveles jerárquicos.
    /// </summary>
    public sealed class RuleTier
    {
        private readonly List<RuleExpressionGroup> _expressionGroups = new();

        /// <summary>
        /// Identificador único del tier
        /// </summary>
        public Guid Id { get; private set; }

        /// <summary>
        /// ID de la promoción padre
        /// </summary>
        public Guid PromotionId { get; private set; }

        /// <summary>
        /// Nivel del tier (jerarquía)
        /// </summary>
        public int TierLevel { get; private set; }

        /// <summary>
        /// Orden de evaluación dentro del mismo nivel
        /// </summary>
        public int Order { get; private set; }

        /// <summary>
        /// Días de cooldown específicos para este tier (opcional)
        /// </summary>
        public int? CooldownDays { get; private set; }

        /// <summary>
        /// Grupos de expresiones asociados (solo lectura)
        /// </summary>
        public IReadOnlyList<RuleExpressionGroup> ExpressionGroups => _expressionGroups.AsReadOnly();

        // Constructor privado para EF Core
        private RuleTier() { }

        /// <summary>
        /// Crea un nuevo tier de reglas
        /// </summary>
        /// <param name="promotionId">ID de la promoción</param>
        /// <param name="tierLevel">Nivel del tier</param>
        /// <param name="order">Orden de evaluación</param>
        /// <param name="cooldownDays">Días de cooldown (opcional)</param>
        /// <returns>Nuevo tier de reglas</returns>
        /// <exception cref="ArgumentException">Cuando los parámetros son inválidos</exception>
        public static RuleTier Create(Guid promotionId, int tierLevel, int order, int? cooldownDays)
        {
            if (promotionId == Guid.Empty)
                throw new ArgumentException("El ID de promoción no puede ser vacío");

            if (tierLevel < 1)
                throw new ArgumentException("El nivel del tier debe ser mayor a 0");

            if (order < 0)
                throw new ArgumentException("El orden no puede ser negativo");

            if (cooldownDays.HasValue && cooldownDays < 0)
                throw new ArgumentException("Los días de cooldown no pueden ser negativos");

            return new RuleTier
            {
                Id = Guid.NewGuid(),
                PromotionId = promotionId,
                TierLevel = tierLevel,
                Order = order,
                CooldownDays = cooldownDays
            };
        }

        /// <summary>
        /// Añade un grupo de expresiones al tier
        /// </summary>
        public void AddExpressionGroup(RuleExpressionGroup expressionGroup)
        {
            if (_expressionGroups.Any(eg => eg.Order == expressionGroup.Order))
                throw new ArgumentException($"Ya existe un grupo de expresiones con el orden {expressionGroup.Order}");

            _expressionGroups.Add(expressionGroup);
        }
    }

    // ========================================
    // ENTITY: RULE EXPRESSION GROUP
    // ========================================

    /// <summary>
    /// Entidad que representa un grupo de expresiones de reglas.
    /// Contiene la lógica de evaluación en formato JSON.
    /// </summary>
    public sealed class RuleExpressionGroup
    {
        private readonly List<RuleGroupReward> _associatedRewards = new();

        /// <summary>
        /// Identificador único del grupo de expresiones
        /// </summary>
        public Guid Id { get; private set; }

        /// <summary>
        /// ID de la promoción padre
        /// </summary>
        public Guid PromotionId { get; private set; }

        /// <summary>
        /// ID del tier padre
        /// </summary>
        public Guid TierId { get; private set; }

        /// <summary>
        /// Orden de evaluación dentro del tier
        /// </summary>
        public int Order { get; private set; }

        /// <summary>
        /// Expresión de reglas en formato JSON
        /// </summary>
        public string ExpressionJson { get; private set; } = default!;

        /// <summary>
        /// Recompensas asociadas al grupo (solo lectura)
        /// </summary>
        public IReadOnlyList<RuleGroupReward> AssociatedRewards => _associatedRewards.AsReadOnly();

        // Constructor privado para EF Core
        private RuleExpressionGroup() { }

        /// <summary>
        /// Crea un nuevo grupo de expresiones de reglas
        /// </summary>
        /// <param name="promotionId">ID de la promoción</param>
        /// <param name="tierId">ID del tier</param>
        /// <param name="order">Orden de evaluación</param>
        /// <param name="expressionJson">Expresión en formato JSON</param>
        /// <returns>Nuevo grupo de expresiones</returns>
        /// <exception cref="ArgumentException">Cuando los parámetros son inválidos</exception>
        public static RuleExpressionGroup Create(Guid promotionId, Guid tierId, int order, string expressionJson)
        {
            if (promotionId == Guid.Empty)
                throw new ArgumentException("El ID de promoción no puede ser vacío");

            if (tierId == Guid.Empty)
                throw new ArgumentException("El ID del tier no puede ser vacío");

            if (order < 0)
                throw new ArgumentException("El orden no puede ser negativo");

            if (string.IsNullOrWhiteSpace(expressionJson))
                throw new ArgumentException("La expresión JSON no puede estar vacía");

            return new RuleExpressionGroup
            {
                Id = Guid.NewGuid(),
                PromotionId = promotionId,
                TierId = tierId,
                Order = order,
                ExpressionJson = expressionJson.Trim()
            };
        }

        /// <summary>
        /// Actualiza la expresión JSON del grupo
        /// </summary>
        /// <param name="newExpressionJson">Nueva expresión JSON</param>
        /// <exception cref="ArgumentException">Cuando la expresión es inválida</exception>
        public void UpdateExpression(string newExpressionJson)
        {
            if (string.IsNullOrWhiteSpace(newExpressionJson))
                throw new ArgumentException("La expresión JSON no puede estar vacía");

            ExpressionJson = newExpressionJson.Trim();
        }

        /// <summary>
        /// Asocia una recompensa al grupo de expresiones
        /// </summary>
        public void AssociateReward(Guid rewardId)
        {
            if (_associatedRewards.Any(ar => ar.RewardId == rewardId))
                throw new ArgumentException("La recompensa ya está asociada a este grupo");

            _associatedRewards.Add(new RuleGroupReward { ExpressionGroupId = Id, RewardId = rewardId });
        }
    }

    // ========================================
    // AGGREGATE ROOT: REWARD
    // ========================================

    /// <summary>
    /// Agregado raíz que representa una recompensa en el sistema.
    /// Las recompensas pueden ser cupones, puntos, regalos o cashback.
    /// </summary>
    public sealed class Reward
    {
        /// <summary>
        /// Identificador único de la recompensa
        /// </summary>
        public Guid Id { get; private set; }

        /// <summary>
        /// Nombre descriptivo de la recompensa
        /// </summary>
        public string Name { get; private set; } = default!;

        /// <summary>
        /// Tipo de recompensa
        /// </summary>
        public RewardType Type { get; private set; }

        /// <summary>
        /// Valor monetario de la recompensa
        /// </summary>
        public MonetaryValue Value { get; private set; } = default!;

        /// <summary>
        /// Indica si la recompensa está activa
        /// </summary>
        public bool IsActive { get; private set; } = true;

        /// <summary>
        /// Fecha de creación de la recompensa
        /// </summary>
        public DateTimeOffset CreatedAt { get; private set; }

        // Constructor privado para EF Core
        private Reward() { }

        /// <summary>
        /// Crea una nueva recompensa
        /// </summary>
        /// <param name="id">Identificador único</param>
        /// <param name="name">Nombre de la recompensa</param>
        /// <param name="type">Tipo de recompensa</param>
        /// <param name="value">Valor monetario</param>
        /// <returns>Nueva recompensa</returns>
        /// <exception cref="ArgumentException">Cuando los parámetros son inválidos</exception>
        public static Reward Create(Guid id, string name, RewardType type, MonetaryValue value)
        {
            if (id == Guid.Empty)
                throw new ArgumentException("El ID no puede ser vacío");

            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("El nombre no puede estar vacío");

            if (value == null)
                throw new ArgumentException("El valor no puede ser nulo");

            return new Reward
            {
                Id = id,
                Name = name.Trim(),
                Type = type,
                Value = value,
                IsActive = true,
                CreatedAt = DateTimeOffset.UtcNow
            };
        }

        /// <summary>
        /// Activa la recompensa
        /// </summary>
        public void Activate()
        {
            IsActive = true;
        }

        /// <summary>
        /// Desactiva la recompensa
        /// </summary>
        public void Deactivate()
        {
            IsActive = false;
        }

        /// <summary>
        /// Actualiza el valor de la recompensa
        /// </summary>
        /// <param name="newValue">Nuevo valor monetario</param>
        /// <exception cref="ArgumentNullException">Cuando el valor es nulo</exception>
        public void UpdateValue(MonetaryValue newValue)
        {
            Value = newValue ?? throw new ArgumentNullException(nameof(newValue));
        }
    }

    // ========================================
    // ENTITY: PROMOTION REWARD (Relationship)
    // ========================================

    /// <summary>
    /// Entidad de relación que asocia promociones con recompensas disponibles
    /// </summary>
    public sealed class PromotionReward
    {
        /// <summary>
        /// ID de la promoción
        /// </summary>
        public Guid PromotionId { get; private set; }

        /// <summary>
        /// ID de la recompensa
        /// </summary>
        public Guid RewardId { get; private set; }

        // Constructor privado para EF Core
        private PromotionReward() { }

        /// <summary>
        /// Crea una nueva asociación promoción-recompensa
        /// </summary>
        /// <param name="promotionId">ID de la promoción</param>
        /// <param name="rewardId">ID de la recompensa</param>
        /// <returns>Nueva asociación</returns>
        /// <exception cref="ArgumentException">Cuando los IDs son inválidos</exception>
        public static PromotionReward Create(Guid promotionId, Guid rewardId)
        {
            if (promotionId == Guid.Empty)
                throw new ArgumentException("El ID de promoción no puede ser vacío");

            if (rewardId == Guid.Empty)
                throw new ArgumentException("El ID de recompensa no puede ser vacío");

            return new PromotionReward
            {
                PromotionId = promotionId,
                RewardId = rewardId
            };
        }
    }

    // ========================================
    // ENTITY: RULE GROUP REWARD (Relationship)
    // ========================================

    /// <summary>
    /// Entidad de relación que asocia grupos de expresiones con recompensas específicas
    /// </summary>
    public sealed class RuleGroupReward
    {
        /// <summary>
        /// ID del grupo de expresiones
        /// </summary>
        public Guid ExpressionGroupId { get; internal set; }

        /// <summary>
        /// ID de la recompensa
        /// </summary>
        public Guid RewardId { get; internal set; }

        // Constructor interno para uso por RuleExpressionGroup
        internal RuleGroupReward() { }
    }

    // ========================================
    // AGGREGATE ROOT: CONTACT REWARD
    // ========================================

    /// <summary>
    /// Agregado raíz que representa el otorgamiento de una recompensa a un contacto.
    /// Mantiene el historial y estado de las recompensas otorgadas.
    /// </summary>
    public sealed class ContactReward
    {
        /// <summary>
        /// Identificador único del otorgamiento
        /// </summary>
        public Guid Id { get; private set; }

        /// <summary>
        /// ID del contacto beneficiario
        /// </summary>
        public Guid ContactId { get; private set; }

        /// <summary>
        /// ID de la promoción que generó la recompensa
        /// </summary>
        public Guid PromotionId { get; private set; }

        /// <summary>
        /// ID de la recompensa otorgada (opcional si es una recompensa calculada)
        /// </summary>
        public Guid? RewardId { get; private set; }

        /// <summary>
        /// ID del grupo de expresiones que activó la recompensa (opcional)
        /// </summary>
        public Guid? ExpressionGroupId { get; private set; }

        /// <summary>
        /// Nivel del tier que activó la recompensa
        /// </summary>
        public int TierLevel { get; private set; }

        /// <summary>
        /// Fecha y hora de otorgamiento
        /// </summary>
        public DateTimeOffset GrantedAt { get; private set; }

        /// <summary>
        /// Estado actual del otorgamiento
        /// </summary>
        public RewardGrantStatus Status { get; private set; }

        /// <summary>
        /// Valor monetario otorgado
        /// </summary>
        public MonetaryValue GrantedValue { get; private set; } = default!;

        /// <summary>
        /// Fecha hasta la cual aplica el cooldown (opcional)
        /// </summary>
        public DateTimeOffset? CooldownUntil { get; private set; }

        /// <summary>
        /// ID del evento que originó el otorgamiento (trazabilidad)
        /// </summary>
        public string? SourceEventId { get; private set; }

        // Constructor privado para EF Core
        private ContactReward() { }

        /// <summary>
        /// Crea un nuevo otorgamiento de recompensa
        /// </summary>
        /// <param name="contactId">ID del contacto</param>
        /// <param name="promotionId">ID de la promoción</param>
        /// <param name="tierLevel">Nivel del tier</param>
        /// <param name="grantedValue">Valor otorgado</param>
        /// <param name="sourceEventId">ID del evento origen</param>
        /// <param name="rewardId">ID de la recompensa (opcional)</param>
        /// <param name="expressionGroupId">ID del grupo de expresiones (opcional)</param>
        /// <param name="cooldownUntil">Fecha límite del cooldown (opcional)</param>
        /// <returns>Nuevo otorgamiento de recompensa</returns>
        /// <exception cref="ArgumentException">Cuando los parámetros son inválidos</exception>
        public static ContactReward Create(
            Guid contactId,
            Guid promotionId,
            int tierLevel,
            MonetaryValue grantedValue,
            string? sourceEventId = null,
            Guid? rewardId = null,
            Guid? expressionGroupId = null,
            DateTimeOffset? cooldownUntil = null)
        {
            if (contactId == Guid.Empty)
                throw new ArgumentException("El ID del contacto no puede ser vacío");

            if (promotionId == Guid.Empty)
                throw new ArgumentException("El ID de promoción no puede ser vacío");

            if (tierLevel < 1)
                throw new ArgumentException("El nivel del tier debe ser mayor a 0");

            if (grantedValue == null)
                throw new ArgumentException("El valor otorgado no puede ser nulo");

            return new ContactReward
            {
                Id = Guid.NewGuid(),
                ContactId = contactId,
                PromotionId = promotionId,
                RewardId = rewardId,
                ExpressionGroupId = expressionGroupId,
                TierLevel = tierLevel,
                GrantedAt = DateTimeOffset.UtcNow,
                Status = RewardGrantStatus.Pending,
                GrantedValue = grantedValue,
                CooldownUntil = cooldownUntil,
                SourceEventId = sourceEventId
            };
        }

        /// <summary>
        /// Marca la recompensa como otorgada exitosamente
        /// </summary>
        /// <exception cref="InvalidOperationException">Cuando el estado no permite esta transición</exception>
        public void MarkAsGranted()
        {
            if (Status == RewardGrantStatus.Rejected)
                throw new InvalidOperationException("No se puede otorgar una recompensa que fue rechazada");

            Status = RewardGrantStatus.Granted;
        }

        /// <summary>
        /// Marca la recompensa como rechazada
        /// </summary>
        /// <exception cref="InvalidOperationException">Cuando el estado no permite esta transición</exception>
        public void MarkAsRejected()
        {
            if (Status == RewardGrantStatus.Granted)
                throw new InvalidOperationException("No se puede rechazar una recompensa ya otorgada");

            Status = RewardGrantStatus.Rejected;
        }

        /// <summary>
        /// Verifica si el cooldown está activo en una fecha determinada
        /// </summary>
        /// <param name="date">Fecha a verificar</param>
        /// <returns>True si el cooldown está activo</returns>
        public bool IsInCooldown(DateTimeOffset date)
        {
            return CooldownUntil.HasValue && date < CooldownUntil.Value;
        }
    }

    // ========================================
    // ENTITY: OUTBOX MESSAGE
    // ========================================

    /// <summary>
    /// Entidad que representa un mensaje en el patrón Outbox para garantizar consistencia eventual.
    /// Almacena eventos de dominio para posterior procesamiento.
    /// </summary>
    public sealed class OutboxMessage
    {
        /// <summary>
        /// Identificador único del mensaje
        /// </summary>
        public Guid Id { get; private set; }

        /// <summary>
        /// Fecha y hora cuando ocurrió el evento
        /// </summary>
        public DateTimeOffset OccurredAt { get; private set; }

        /// <summary>
        /// Tipo de evento/mensaje
        /// </summary>
        public string Type { get; private set; } = default!;

        /// <summary>
        /// Payload del mensaje en formato JSON
        /// </summary>
        public string Payload { get; private set; } = default!;

        /// <summary>
        /// Indica si el mensaje ya fue procesado
        /// </summary>
        public bool IsProcessed { get; private set; }

        /// <summary>
        /// Fecha de procesamiento (si aplica)
        /// </summary>
        public DateTimeOffset? ProcessedAt { get; private set; }

        // Constructor privado para EF Core
        private OutboxMessage() { }

        /// <summary>
        /// Crea un nuevo mensaje de outbox
        /// </summary>
        /// <param name="type">Tipo de mensaje</param>
        /// <param name="payload">Payload en JSON</param>
        /// <param name="occurredAt">Fecha del evento (opcional, usa UTC actual por defecto)</param>
        /// <returns>Nuevo mensaje de outbox</returns>
        /// <exception cref="ArgumentException">Cuando los parámetros son inválidos</exception>
        public static OutboxMessage Create(string type, string payload, DateTimeOffset? occurredAt = null)
        {
            if (string.IsNullOrWhiteSpace(type))
                throw new ArgumentException("El tipo no puede estar vacío");

            if (string.IsNullOrWhiteSpace(payload))
                throw new ArgumentException("El payload no puede estar vacío");

            return new OutboxMessage
            {
                Id = Guid.NewGuid(),
                OccurredAt = occurredAt ?? DateTimeOffset.UtcNow,
                Type = type.Trim(),
                Payload = payload.Trim(),
                IsProcessed = false
            };
        }

        /// <summary>
        /// Marca el mensaje como procesado
        /// </summary>
        /// <exception cref="InvalidOperationException">Cuando el mensaje ya fue procesado</exception>
        public void MarkAsProcessed()
        {
            if (IsProcessed)
                throw new InvalidOperationException("El mensaje ya fue procesado");

            IsProcessed = true;
            ProcessedAt = DateTimeOffset.UtcNow;
        }
    }

    // ========================================
    // ENTITY: ATTRIBUTE CATALOG
    // ========================================

    /// <summary>
    /// Entidad que representa un atributo en el catálogo del sistema.
    /// Define los metadatos de los atributos disponibles para las reglas de negocio.
    /// </summary>
    public sealed class AttributeCatalog
    {
        /// <summary>
        /// Identificador único del atributo
        /// </summary>
        public Guid Id { get; private set; }

        /// <summary>
        /// Nombre lógico de la entidad (ej: contact, account)
        /// </summary>
        public string EntityLogicalName { get; private set; } = "contact";

        /// <summary>
        /// Nombre lógico del atributo en la entidad
        /// </summary>
        public string AttributeLogicalName { get; private set; } = default!;

        /// <summary>
        /// Nombre canónico para visualización
        /// </summary>
        public string CanonicalName { get; private set; } = default!;

        /// <summary>
        /// Tipo de datos del atributo
        /// </summary>
        public DataType DataType { get; private set; }

        /// <summary>
        /// Indica si el atributo está expuesto para uso en reglas
        /// </summary>
        public bool IsExposed { get; private set; }

        /// <summary>
        /// Fecha de creación del registro
        /// </summary>
        public DateTimeOffset CreatedAt { get; private set; }

        // Constructor privado para EF Core
        private AttributeCatalog() { }

        /// <summary>
        /// Crea una nueva entrada en el catálogo de atributos
        /// </summary>
        /// <param name="entityLogicalName">Nombre lógico de la entidad</param>
        /// <param name="attributeLogicalName">Nombre lógico del atributo</param>
        /// <param name="canonicalName">Nombre canónico</param>
        /// <param name="dataType">Tipo de datos</param>
        /// <param name="isExposed">Si está expuesto para reglas</param>
        /// <returns>Nueva entrada del catálogo</returns>
        /// <exception cref="ArgumentException">Cuando los parámetros son inválidos</exception>
        public static AttributeCatalog Create(
            string entityLogicalName,
            string attributeLogicalName,
            string canonicalName,
            DataType dataType,
            bool isExposed = true)
        {
            if (string.IsNullOrWhiteSpace(entityLogicalName))
                throw new ArgumentException("El nombre lógico de la entidad no puede estar vacío");

            if (string.IsNullOrWhiteSpace(attributeLogicalName))
                throw new ArgumentException("El nombre lógico del atributo no puede estar vacío");

            if (string.IsNullOrWhiteSpace(canonicalName))
                throw new ArgumentException("El nombre canónico no puede estar vacío");

            return new AttributeCatalog
            {
                Id = Guid.NewGuid(),
                EntityLogicalName = entityLogicalName.Trim().ToLowerInvariant(),
                AttributeLogicalName = attributeLogicalName.Trim().ToLowerInvariant(),
                CanonicalName = canonicalName.Trim(),
                DataType = dataType,
                IsExposed = isExposed,
                CreatedAt = DateTimeOffset.UtcNow
            };
        }

        /// <summary>
        /// Expone el atributo para uso en reglas
        /// </summary>
        public void Expose()
        {
            IsExposed = true;
        }

        /// <summary>
        /// Oculta el atributo del uso en reglas
        /// </summary>
        public void Hide()
        {
            IsExposed = false;
        }

        /// <summary>
        /// Actualiza el nombre canónico del atributo
        /// </summary>
        /// <param name="newCanonicalName">Nuevo nombre canónico</param>
        /// <exception cref="ArgumentException">Cuando el nombre es inválido</exception>
        public void UpdateCanonicalName(string newCanonicalName)
        {
            if (string.IsNullOrWhiteSpace(newCanonicalName))
                throw new ArgumentException("El nombre canónico no puede estar vacío");

            CanonicalName = newCanonicalName.Trim();
        }
    }

    // ========================================
    // AGGREGATE ROOT: OPERATOR CATALOG
    // ========================================

    /// <summary>
    /// Agregado raíz que representa un operador en el catálogo del sistema.
    /// Define los operadores disponibles para construir expresiones de reglas.
    /// </summary>
    public sealed class OperatorCatalog
    {
        private readonly List<OperatorSupportedType> _supportedTypes = new();

        /// <summary>
        /// Identificador único del operador
        /// </summary>
        public Guid Id { get; private set; }

        /// <summary>
        /// Código único del operador (ej: equals, contains, greater_than)
        /// </summary>
        public string Code { get; private set; } = default!;

        /// <summary>
        /// Nombre para mostrar al usuario
        /// </summary>
        public string DisplayName { get; private set; } = default!;

        /// <summary>
        /// Indica si el operador está activo
        /// </summary>
        public bool IsActive { get; private set; } = true;

        /// <summary>
        /// Fecha de creación del registro
        /// </summary>
        public DateTimeOffset CreatedAt { get; private set; }

        /// <summary>
        /// Tipos de datos soportados por el operador (solo lectura)
        /// </summary>
        public IReadOnlyList<OperatorSupportedType> SupportedTypes => _supportedTypes.AsReadOnly();

        // Constructor privado para EF Core
        private OperatorCatalog() { }

        /// <summary>
        /// Crea una nueva entrada en el catálogo de operadores
        /// </summary>
        /// <param name="code">Código único del operador</param>
        /// <param name="displayName">Nombre para mostrar</param>
        /// <returns>Nueva entrada del catálogo</returns>
        /// <exception cref="ArgumentException">Cuando los parámetros son inválidos</exception>
        public static OperatorCatalog Create(string code, string displayName)
        {
            if (string.IsNullOrWhiteSpace(code))
                throw new ArgumentException("El código no puede estar vacío");

            if (string.IsNullOrWhiteSpace(displayName))
                throw new ArgumentException("El nombre para mostrar no puede estar vacío");

            return new OperatorCatalog
            {
                Id = Guid.NewGuid(),
                Code = code.Trim().ToLowerInvariant(),
                DisplayName = displayName.Trim(),
                IsActive = true,
                CreatedAt = DateTimeOffset.UtcNow
            };
        }

        /// <summary>
        /// Añade soporte para un tipo de datos específico
        /// </summary>
        /// <param name="dataType">Tipo de datos a soportar</param>
        /// <exception cref="ArgumentException">Cuando el tipo ya está soportado</exception>
        public void AddSupportedType(DataType dataType)
        {
            if (_supportedTypes.Any(st => st.DataType == dataType))
                throw new ArgumentException($"El tipo {dataType} ya está soportado por este operador");

            _supportedTypes.Add(new OperatorSupportedType { OperatorId = Id, DataType = dataType });
        }

        /// <summary>
        /// Verifica si el operador soporta un tipo de datos específico
        /// </summary>
        /// <param name="dataType">Tipo de datos a verificar</param>
        /// <returns>True si el tipo está soportado</returns>
        public bool SupportsType(DataType dataType)
        {
            return _supportedTypes.Any(st => st.DataType == dataType);
        }

        /// <summary>
        /// Activa el operador
        /// </summary>
        public void Activate()
        {
            IsActive = true;
        }

        /// <summary>
        /// Desactiva el operador
        /// </summary>
        public void Deactivate()
        {
            IsActive = false;
        }

        /// <summary>
        /// Actualiza el nombre para mostrar
        /// </summary>
        /// <param name="newDisplayName">Nuevo nombre para mostrar</param>
        /// <exception cref="ArgumentException">Cuando el nombre es inválido</exception>
        public void UpdateDisplayName(string newDisplayName)
        {
            if (string.IsNullOrWhiteSpace(newDisplayName))
                throw new ArgumentException("El nombre para mostrar no puede estar vacío");

            DisplayName = newDisplayName.Trim();
        }
    }

    // ========================================
    // ENTITY: OPERATOR SUPPORTED TYPE (Relationship)
    // ========================================

    /// <summary>
    /// Entidad de relación que define qué tipos de datos soporta cada operador
    /// </summary>
    public sealed class OperatorSupportedType
    {
        /// <summary>
        /// ID del operador
        /// </summary>
        public Guid OperatorId { get; internal set; }

        /// <summary>
        /// Tipo de datos soportado
        /// </summary>
        public DataType DataType { get; internal set; }

        // Constructor interno para uso por OperatorCatalog
        internal OperatorSupportedType() { }
    }
}