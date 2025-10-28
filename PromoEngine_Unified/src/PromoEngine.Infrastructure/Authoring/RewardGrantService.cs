using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PromoEngine.Application;
using PromoEngine.Domain;
using PromoEngine.Infrastructure.EF;

namespace PromoEngine.Infrastructure.Authoring
{
    /// <summary>
    /// Servicio de infraestructura que implementa el otorgamiento de recompensas.
    /// Actúa como adaptador entre la capa de aplicación y la persistencia de datos.
    /// Implementa el patrón Repository implícito a través de Entity Framework.
    /// </summary>
    public sealed class RewardGrantService : IRewardGrantService
    {
        private readonly PromoEngineDbContext _dbContext;
        private readonly ILogger<RewardGrantService> _logger;

        /// <summary>
        /// Inicializa una nueva instancia del servicio de otorgamiento de recompensas
        /// </summary>
        /// <param name="dbContext">Contexto de base de datos de Entity Framework</param>
        /// <param name="logger">Logger para trazabilidad y diagnósticos</param>
        /// <exception cref="ArgumentNullException">Cuando alguno de los parámetros es nulo</exception>
        public RewardGrantService(
            PromoEngineDbContext dbContext, 
            ILogger<RewardGrantService> logger)
        {
            _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>
        /// Otorga recompensas a un contacto específico basándose en las reglas de promoción evaluadas.
        /// Implementa idempotencia para evitar otorgamientos duplicados del mismo evento.
        /// </summary>
        /// <param name="contactId">Identificador único del contacto beneficiario</param>
        /// <param name="promotion">Promoción que genera las recompensas</param>
        /// <param name="promotionVersion">Versión específica de la promoción</param>
        /// <param name="tierLevel">Nivel del tier que activó la recompensa</param>
        /// <param name="expressionGroupId">Identificador del grupo de expresiones que activó la regla (opcional)</param>
        /// <param name="rewardIds">Lista de identificadores de recompensas específicas a otorgar</param>
        /// <param name="eventContext">Contexto del evento que originó el otorgamiento</param>
        /// <param name="grantedAt">Fecha y hora del otorgamiento</param>
        /// <param name="tierCooldownDays">Días de cooldown específicos del tier (opcional)</param>
        /// <param name="cancellationToken">Token de cancelación para operaciones asíncronas</param>
        /// <returns>Task que representa la operación asíncrona</returns>
        /// <exception cref="ArgumentException">Cuando los parámetros de entrada son inválidos</exception>
        /// <exception cref="InvalidOperationException">Cuando ocurre un error en el proceso de otorgamiento</exception>
        public async Task GrantAsync(
            Guid contactId,
            Promotion promotion,
            PromotionVersion promotionVersion,
            int tierLevel,
            Guid? expressionGroupId,
            IReadOnlyList<Guid> rewardIds,
            EventContextDto eventContext,
            DateTimeOffset grantedAt,
            int? tierCooldownDays,
            CancellationToken cancellationToken = default)
        {
            // Validación de parámetros de entrada
            ValidateGrantParameters(contactId, promotion, promotionVersion, tierLevel, rewardIds, eventContext, grantedAt);

            _logger.LogInformation(
                "Iniciando otorgamiento de recompensas. ContactId: {ContactId}, PromotionId: {PromotionId}, TierLevel: {TierLevel}, EventId: {EventId}",
                contactId, promotion.Id, tierLevel, eventContext.EventId);

            try
            {
                // Verificar idempotencia basada en el ID del evento fuente
                if (await IsEventAlreadyProcessedAsync(contactId, promotion.Id, eventContext.EventId, cancellationToken))
                {
                    _logger.LogInformation(
                        "El evento {EventId} ya fue procesado para el contacto {ContactId} y promoción {PromotionId}. Operación omitida.",
                        eventContext.EventId, contactId, promotion.Id);
                    return;
                }

                // Calcular fecha límite del cooldown
                var cooldownUntil = CalculateCooldownDate(grantedAt, tierCooldownDays, promotion.GlobalCooldownDays);

                // Crear otorgamientos de recompensas
                var contactRewards = CreateContactRewards(
                    contactId,
                    promotion,
                    tierLevel,
                    expressionGroupId,
                    rewardIds,
                    eventContext,
                    grantedAt,
                    cooldownUntil);

                // Persistir otorgamientos en base de datos
                await PersistRewardGrantsAsync(contactRewards, cancellationToken);

                _logger.LogInformation(
                    "Otorgamiento completado exitosamente. {RewardCount} recompensas otorgadas al contacto {ContactId}",
                    contactRewards.Count, contactId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Error durante el otorgamiento de recompensas. ContactId: {ContactId}, PromotionId: {PromotionId}, EventId: {EventId}",
                    contactId, promotion.Id, eventContext.EventId);

                throw new InvalidOperationException(
                    $"Error al otorgar recompensas al contacto {contactId} para la promoción {promotion.Id}", ex);
            }
        }

        /// <summary>
        /// Valida los parámetros de entrada del método GrantAsync
        /// </summary>
        /// <param name="contactId">ID del contacto</param>
        /// <param name="promotion">Promoción</param>
        /// <param name="promotionVersion">Versión de promoción</param>
        /// <param name="tierLevel">Nivel del tier</param>
        /// <param name="rewardIds">IDs de recompensas</param>
        /// <param name="eventContext">Contexto del evento</param>
        /// <param name="grantedAt">Fecha de otorgamiento</param>
        /// <exception cref="ArgumentException">Cuando algún parámetro es inválido</exception>
        private static void ValidateGrantParameters(
            Guid contactId,
            Promotion promotion,
            PromotionVersion promotionVersion,
            int tierLevel,
            IReadOnlyList<Guid> rewardIds,
            EventContextDto eventContext,
            DateTimeOffset grantedAt)
        {
            if (contactId == Guid.Empty)
                throw new ArgumentException("El ID del contacto no puede ser vacío", nameof(contactId));

            if (promotion == null)
                throw new ArgumentNullException(nameof(promotion));

            if (promotionVersion == null)
                throw new ArgumentNullException(nameof(promotionVersion));

            if (tierLevel <= 0)
                throw new ArgumentException("El nivel del tier debe ser mayor a cero", nameof(tierLevel));

            if (rewardIds == null)
                throw new ArgumentNullException(nameof(rewardIds));

            if (eventContext == null)
                throw new ArgumentNullException(nameof(eventContext));

            if (grantedAt == default)
                throw new ArgumentException("La fecha de otorgamiento debe ser válida", nameof(grantedAt));

            // Validar que la promoción y versión estén relacionadas
            if (promotionVersion.PromotionId != promotion.Id)
                throw new ArgumentException("La versión de promoción no pertenece a la promoción especificada");
        }

        /// <summary>
        /// Verifica si un evento específico ya fue procesado para evitar otorgamientos duplicados
        /// </summary>
        /// <param name="contactId">ID del contacto</param>
        /// <param name="promotionId">ID de la promoción</param>
        /// <param name="eventId">ID del evento fuente</param>
        /// <param name="cancellationToken">Token de cancelación</param>
        /// <returns>True si el evento ya fue procesado</returns>
        private async Task<bool> IsEventAlreadyProcessedAsync(
            Guid contactId,
            Guid promotionId,
            string? eventId,
            CancellationToken cancellationToken)
        {
            // Si no hay ID de evento, no se puede verificar idempotencia
            if (string.IsNullOrWhiteSpace(eventId))
            {
                _logger.LogWarning(
                    "No se proporcionó EventId para verificar idempotencia. ContactId: {ContactId}, PromotionId: {PromotionId}",
                    contactId, promotionId);
                return false;
            }

            var alreadyProcessed = await _dbContext.ContactRewards
                .AnyAsync(cr =>
                    cr.ContactId == contactId &&
                    cr.PromotionId == promotionId &&
                    cr.SourceEventId == eventId &&
                    cr.Status == RewardGrantStatus.Granted,
                    cancellationToken);

            return alreadyProcessed;
        }

        /// <summary>
        /// Calcula la fecha hasta la cual aplica el cooldown basándose en las reglas de tier y globales
        /// </summary>
        /// <param name="grantedAt">Fecha de otorgamiento</param>
        /// <param name="tierCooldownDays">Días de cooldown específicos del tier</param>
        /// <param name="globalCooldownDays">Días de cooldown globales de la promoción</param>
        /// <returns>Fecha límite del cooldown o null si no aplica cooldown</returns>
        private DateTimeOffset? CalculateCooldownDate(
            DateTimeOffset grantedAt,
            int? tierCooldownDays,
            int globalCooldownDays)
        {
            // Usar cooldown del tier si está especificado, caso contrario usar el global
            var effectiveCooldownDays = tierCooldownDays ?? globalCooldownDays;

            // Solo aplicar cooldown si es mayor a cero
            if (effectiveCooldownDays <= 0)
            {
                _logger.LogDebug("No se aplica cooldown (días efectivos: {EffectiveDays})", effectiveCooldownDays);
                return null;
            }

            var cooldownUntil = grantedAt.AddDays(effectiveCooldownDays);

            _logger.LogDebug(
                "Cooldown calculado: {CooldownDays} días hasta {CooldownUntil}",
                effectiveCooldownDays, cooldownUntil);

            return cooldownUntil;
        }

        /// <summary>
        /// Crea las instancias de ContactReward basándose en los parámetros de otorgamiento
        /// </summary>
        /// <param name="contactId">ID del contacto</param>
        /// <param name="promotion">Promoción</param>
        /// <param name="tierLevel">Nivel del tier</param>
        /// <param name="expressionGroupId">ID del grupo de expresiones</param>
        /// <param name="rewardIds">IDs de recompensas específicas</param>
        /// <param name="eventContext">Contexto del evento</param>
        /// <param name="grantedAt">Fecha de otorgamiento</param>
        /// <param name="cooldownUntil">Fecha límite del cooldown</param>
        /// <returns>Lista de otorgamientos de recompensas</returns>
        private List<ContactReward> CreateContactRewards(
            Guid contactId,
            Promotion promotion,
            int tierLevel,
            Guid? expressionGroupId,
            IReadOnlyList<Guid> rewardIds,
            EventContextDto eventContext,
            DateTimeOffset grantedAt,
            DateTimeOffset? cooldownUntil)
        {
            var contactRewards = new List<ContactReward>();

            if (rewardIds.Count == 0)
            {
                // Crear otorgamiento sin recompensa específica (recompensa calculada)
                var genericReward = CreateSingleContactReward(
                    contactId,
                    promotion,
                    tierLevel,
                    expressionGroupId,
                    rewardId: null,
                    eventContext,
                    grantedAt,
                    cooldownUntil);

                contactRewards.Add(genericReward);

                _logger.LogDebug(
                    "Creado otorgamiento genérico para contacto {ContactId} sin recompensa específica",
                    contactId);
            }
            else
            {
                // Crear otorgamiento para cada recompensa específica
                foreach (var rewardId in rewardIds)
                {
                    var specificReward = CreateSingleContactReward(
                        contactId,
                        promotion,
                        tierLevel,
                        expressionGroupId,
                        rewardId,
                        eventContext,
                        grantedAt,
                        cooldownUntil);

                    contactRewards.Add(specificReward);
                }

                _logger.LogDebug(
                    "Creados {Count} otorgamientos específicos para contacto {ContactId}",
                    rewardIds.Count, contactId);
            }

            return contactRewards;
        }

        /// <summary>
        /// Crea una instancia individual de ContactReward
        /// </summary>
        /// <param name="contactId">ID del contacto</param>
        /// <param name="promotion">Promoción</param>
        /// <param name="tierLevel">Nivel del tier</param>
        /// <param name="expressionGroupId">ID del grupo de expresiones</param>
        /// <param name="rewardId">ID de la recompensa específica (opcional)</param>
        /// <param name="eventContext">Contexto del evento</param>
        /// <param name="grantedAt">Fecha de otorgamiento</param>
        /// <param name="cooldownUntil">Fecha límite del cooldown</param>
        /// <returns>Instancia de ContactReward</returns>
        private static ContactReward CreateSingleContactReward(
            Guid contactId,
            Promotion promotion,
            int tierLevel,
            Guid? expressionGroupId,
            Guid? rewardId,
            EventContextDto eventContext,
            DateTimeOffset grantedAt,
            DateTimeOffset? cooldownUntil)
        {
            // Usar el factory method del dominio para crear la instancia
            // Esto garantiza que se apliquen las invariantes de dominio
            var monetaryValue = MonetaryValue.Create(0, "USD"); // Valor por defecto, se calculará posteriormente

            return ContactReward.Create(
                contactId: contactId,
                promotionId: promotion.Id,
                tierLevel: tierLevel,
                grantedValue: monetaryValue,
                sourceEventId: eventContext.EventId,
                rewardId: rewardId,
                expressionGroupId: expressionGroupId,
                cooldownUntil: cooldownUntil);
        }

        /// <summary>
        /// Persiste los otorgamientos de recompensas en la base de datos de forma transaccional
        /// </summary>
        /// <param name="contactRewards">Lista de otorgamientos a persistir</param>
        /// <param name="cancellationToken">Token de cancelación</param>
        /// <returns>Task que representa la operación asíncrona</returns>
        /// <exception cref="InvalidOperationException">Cuando ocurre un error de persistencia</exception>
        private async Task PersistRewardGrantsAsync(
            IEnumerable<ContactReward> contactRewards,
            CancellationToken cancellationToken)
        {
            var rewardsList = contactRewards.ToList();

            if (rewardsList.Count == 0)
            {
                _logger.LogWarning("No hay otorgamientos para persistir");
                return;
            }

            try
            {
                // Usar transacción implícita de SaveChangesAsync para garantizar atomicidad
                await _dbContext.ContactRewards.AddRangeAsync(rewardsList, cancellationToken);
                
                var savedCount = await _dbContext.SaveChangesAsync(cancellationToken);

                _logger.LogInformation(
                    "Persistidos {SavedCount} otorgamientos de recompensas en la base de datos",
                    savedCount);

                // Verificar que se guardaron todos los registros esperados
                if (savedCount != rewardsList.Count)
                {
                    _logger.LogWarning(
                        "Discrepancia en persistencia: esperados {Expected}, guardados {Actual}",
                        rewardsList.Count, savedCount);
                }
            }
            catch (DbUpdateException dbEx)
            {
                _logger.LogError(dbEx,
                    "Error de base de datos al persistir otorgamientos de recompensas");
                
                throw new InvalidOperationException(
                    "Error al guardar los otorgamientos de recompensas en la base de datos", dbEx);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Error inesperado al persistir otorgamientos de recompensas");
                
                throw new InvalidOperationException(
                    "Error inesperado durante la persistencia de otorgamientos", ex);
            }
        }
    }
}