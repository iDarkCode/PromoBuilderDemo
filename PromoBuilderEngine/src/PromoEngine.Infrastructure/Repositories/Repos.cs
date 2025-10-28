using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PromoEngine.Application;
using PromoEngine.Domain;
using PromoEngine.Infrastructure.EF;

namespace PromoEngine.Infrastructure.Repositories
{
    // ========================================
    // REPOSITORY: RULE TIER
    // ========================================

    /// <summary>
    /// Repositorio para el acceso a datos de RuleTier.
    /// 
    /// Implementa el patrón Repository proporcionando una abstracción sobre
    /// Entity Framework para operaciones de consulta relacionadas con niveles de reglas.
    /// Optimiza las consultas utilizando los índices definidos en el DbContext.
    /// </summary>
    public sealed class RuleTierRepository : IRuleTierRepository
    {
        private readonly PromoEngineDbContext _dbContext;
        private readonly ILogger<RuleTierRepository> _logger;

        /// <summary>
        /// Inicializa una nueva instancia del repositorio de RuleTier
        /// </summary>
        /// <param name="dbContext">Contexto de base de datos</param>
        /// <param name="logger">Logger para trazabilidad</param>
        /// <exception cref="ArgumentNullException">Cuando algún parámetro es nulo</exception>
        public RuleTierRepository(
            PromoEngineDbContext dbContext, 
            ILogger<RuleTierRepository> logger)
        {
            _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>
        /// Obtiene todos los tiers de reglas asociados a una promoción específica,
        /// ordenados por nivel de tier y luego por orden de evaluación.
        /// 
        /// Esta consulta utiliza el índice compuesto ix_rule_tier_promotion_level
        /// para optimizar el rendimiento en consultas frecuentes.
        /// </summary>
        /// <param name="promotionId">Identificador único de la promoción</param>
        /// <param name="cancellationToken">Token de cancelación</param>
        /// <returns>Lista ordenada de tiers de reglas</returns>
        /// <exception cref="ArgumentException">Cuando promotionId es Guid.Empty</exception>
        /// <exception cref="InvalidOperationException">Cuando ocurre un error de base de datos</exception>
        public async Task<IReadOnlyList<RuleTier>> GetTiersAsync(
            Guid promotionId, 
            CancellationToken cancellationToken = default)
        {
            if (promotionId == Guid.Empty)
            {
                throw new ArgumentException("El ID de promoción no puede ser vacío", nameof(promotionId));
            }

            try
            {
                _logger.LogDebug(
                    "Obteniendo tiers para promoción {PromotionId}",
                    promotionId);

                var tiers = await _dbContext.RuleTiers
                    .AsNoTracking() // Optimización para consultas de solo lectura
                    .Where(tier => tier.PromotionId == promotionId)
                    .OrderBy(tier => tier.TierLevel)
                    .ThenBy(tier => tier.Order)
                    .ToListAsync(cancellationToken);

                _logger.LogDebug(
                    "Obtenidos {TierCount} tiers para promoción {PromotionId}",
                    tiers.Count, promotionId);

                return tiers.AsReadOnly();
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Operación cancelada para promoción {PromotionId}", promotionId);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Error obteniendo tiers para promoción {PromotionId}",
                    promotionId);

                throw new InvalidOperationException(
                    $"Error al obtener tiers para la promoción {promotionId}", ex);
            }
        }

        /// <summary>
        /// Obtiene un tier específico por su identificador
        /// </summary>
        /// <param name="tierId">Identificador del tier</param>
        /// <param name="cancellationToken">Token de cancelación</param>
        /// <returns>Tier encontrado o null si no existe</returns>
        public async Task<RuleTier?> GetTierByIdAsync(
            Guid tierId, 
            CancellationToken cancellationToken = default)
        {
            if (tierId == Guid.Empty)
            {
                throw new ArgumentException("El ID del tier no puede ser vacío", nameof(tierId));
            }

            try
            {
                var tier = await _dbContext.RuleTiers
                    .AsNoTracking()
                    .FirstOrDefaultAsync(t => t.Id == tierId, cancellationToken);

                if (tier != null)
                {
                    _logger.LogDebug("Tier {TierId} encontrado para promoción {PromotionId}", 
                        tierId, tier.PromotionId);
                }
                else
                {
                    _logger.LogDebug("Tier {TierId} no encontrado", tierId);
                }

                return tier;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error obteniendo tier {TierId}", tierId);
                throw new InvalidOperationException($"Error al obtener tier {tierId}", ex);
            }
        }

        /// <summary>
        /// Verifica si existe un tier con el nivel especificado para una promoción
        /// </summary>
        /// <param name="promotionId">ID de la promoción</param>
        /// <param name="tierLevel">Nivel del tier a verificar</param>
        /// <param name="cancellationToken">Token de cancelación</param>
        /// <returns>True si existe el tier</returns>
        public async Task<bool> ExistsTierWithLevelAsync(
            Guid promotionId, 
            int tierLevel, 
            CancellationToken cancellationToken = default)
        {
            if (promotionId == Guid.Empty)
            {
                throw new ArgumentException("El ID de promoción no puede ser vacío", nameof(promotionId));
            }

            if (tierLevel <= 0)
            {
                throw new ArgumentException("El nivel del tier debe ser mayor a cero", nameof(tierLevel));
            }

            try
            {
                var exists = await _dbContext.RuleTiers
                    .AsNoTracking()
                    .AnyAsync(t => t.PromotionId == promotionId && t.TierLevel == tierLevel, 
                             cancellationToken);

                _logger.LogDebug(
                    "Verificación de existencia - Promoción: {PromotionId}, Nivel: {TierLevel}, Existe: {Exists}",
                    promotionId, tierLevel, exists);

                return exists;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Error verificando existencia de tier. Promoción: {PromotionId}, Nivel: {TierLevel}",
                    promotionId, tierLevel);

                throw new InvalidOperationException(
                    $"Error al verificar existencia del tier nivel {tierLevel} para promoción {promotionId}", ex);
            }
        }
    }

    // ========================================
    // REPOSITORY: EXPRESSION GROUP
    // ========================================

    /// <summary>
    /// Repositorio para el acceso a datos de RuleExpressionGroup.
    /// 
    /// Proporciona operaciones optimizadas para consultar grupos de expresiones
    /// asociados a tiers específicos, manteniendo el orden de evaluación correcto.
    /// </summary>
    public sealed class ExpressionGroupRepository : IExpressionGroupRepository
    {
        private readonly PromoEngineDbContext _dbContext;
        private readonly ILogger<ExpressionGroupRepository> _logger;

        /// <summary>
        /// Inicializa una nueva instancia del repositorio de grupos de expresiones
        /// </summary>
        /// <param name="dbContext">Contexto de base de datos</param>
        /// <param name="logger">Logger para trazabilidad</param>
        /// <exception cref="ArgumentNullException">Cuando algún parámetro es nulo</exception>
        public ExpressionGroupRepository(
            PromoEngineDbContext dbContext, 
            ILogger<ExpressionGroupRepository> logger)
        {
            _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>
        /// Obtiene todos los grupos de expresiones asociados a un tier específico,
        /// ordenados por su orden de evaluación.
        /// 
        /// Utiliza el índice ix_rule_expression_group_tier_order para optimización.
        /// </summary>
        /// <param name="tierId">Identificador único del tier</param>
        /// <param name="cancellationToken">Token de cancelación</param>
        /// <returns>Lista ordenada de grupos de expresiones</returns>
        /// <exception cref="ArgumentException">Cuando tierId es Guid.Empty</exception>
        /// <exception cref="InvalidOperationException">Cuando ocurre un error de base de datos</exception>
        public async Task<IReadOnlyList<RuleExpressionGroup>> GetGroupsAsync(
            Guid tierId, 
            CancellationToken cancellationToken = default)
        {
            if (tierId == Guid.Empty)
            {
                throw new ArgumentException("El ID del tier no puede ser vacío", nameof(tierId));
            }

            try
            {
                _logger.LogDebug("Obteniendo grupos de expresiones para tier {TierId}", tierId);

                var groups = await _dbContext.ExpressionGroups
                    .AsNoTracking()
                    .Where(group => group.TierId == tierId)
                    .OrderBy(group => group.Order)
                    .ToListAsync(cancellationToken);

                _logger.LogDebug(
                    "Obtenidos {GroupCount} grupos de expresiones para tier {TierId}",
                    groups.Count, tierId);

                return groups.AsReadOnly();
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Operación cancelada para tier {TierId}", tierId);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Error obteniendo grupos de expresiones para tier {TierId}",
                    tierId);

                throw new InvalidOperationException(
                    $"Error al obtener grupos de expresiones para el tier {tierId}", ex);
            }
        }

        /// <summary>
        /// Obtiene un grupo de expresiones específico por su identificador
        /// </summary>
        /// <param name="groupId">Identificador del grupo</param>
        /// <param name="cancellationToken">Token de cancelación</param>
        /// <returns>Grupo encontrado o null si no existe</returns>
        public async Task<RuleExpressionGroup?> GetGroupByIdAsync(
            Guid groupId, 
            CancellationToken cancellationToken = default)
        {
            if (groupId == Guid.Empty)
            {
                throw new ArgumentException("El ID del grupo no puede ser vacío", nameof(groupId));
            }

            try
            {
                var group = await _dbContext.ExpressionGroups
                    .AsNoTracking()
                    .FirstOrDefaultAsync(g => g.Id == groupId, cancellationToken);

                if (group != null)
                {
                    _logger.LogDebug("Grupo {GroupId} encontrado para tier {TierId}", 
                        groupId, group.TierId);
                }
                else
                {
                    _logger.LogDebug("Grupo {GroupId} no encontrado", groupId);
                }

                return group;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error obteniendo grupo {GroupId}", groupId);
                throw new InvalidOperationException($"Error al obtener grupo {groupId}", ex);
            }
        }

        /// <summary>
        /// Obtiene todos los grupos de expresiones para una promoción completa
        /// </summary>
        /// <param name="promotionId">ID de la promoción</param>
        /// <param name="cancellationToken">Token de cancelación</param>
        /// <returns>Lista de todos los grupos de la promoción</returns>
        public async Task<IReadOnlyList<RuleExpressionGroup>> GetGroupsByPromotionAsync(
            Guid promotionId, 
            CancellationToken cancellationToken = default)
        {
            if (promotionId == Guid.Empty)
            {
                throw new ArgumentException("El ID de promoción no puede ser vacío", nameof(promotionId));
            }

            try
            {
                var groups = await _dbContext.ExpressionGroups
                    .AsNoTracking()
                    .Where(g => g.PromotionId == promotionId)
                    .OrderBy(g => g.Order)
                    .ToListAsync(cancellationToken);

                _logger.LogDebug(
                    "Obtenidos {GroupCount} grupos para promoción {PromotionId}",
                    groups.Count, promotionId);

                return groups.AsReadOnly();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Error obteniendo grupos para promoción {PromotionId}",
                    promotionId);

                throw new InvalidOperationException(
                    $"Error al obtener grupos para la promoción {promotionId}", ex);
            }
        }
    }

    // ========================================
    // REPOSITORY: PROMOTION REWARD
    // ========================================

    /// <summary>
    /// Repositorio para el acceso a datos de relaciones entre promociones y recompensas.
    /// 
    /// Gestiona tanto las recompensas globales de promociones como las recompensas
    /// específicas de grupos de expresiones, optimizando consultas frecuentes.
    /// </summary>
    public sealed class PromotionRewardRepository : IPromotionRewardRepository
    {
        private readonly PromoEngineDbContext _dbContext;
        private readonly ILogger<PromotionRewardRepository> _logger;

        /// <summary>
        /// Inicializa una nueva instancia del repositorio de recompensas de promoción
        /// </summary>
        /// <param name="dbContext">Contexto de base de datos</param>
        /// <param name="logger">Logger para trazabilidad</param>
        /// <exception cref="ArgumentNullException">Cuando algún parámetro es nulo</exception>
        public PromotionRewardRepository(
            PromoEngineDbContext dbContext, 
            ILogger<PromotionRewardRepository> logger)
        {
            _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>
        /// Obtiene los identificadores de todas las recompensas globales asociadas a una promoción.
        /// Estas son recompensas que pueden ser otorgadas por cualquier regla de la promoción.
        /// </summary>
        /// <param name="promotionId">Identificador único de la promoción</param>
        /// <param name="cancellationToken">Token de cancelación</param>
        /// <returns>Lista de IDs de recompensas globales</returns>
        /// <exception cref="ArgumentException">Cuando promotionId es Guid.Empty</exception>
        /// <exception cref="InvalidOperationException">Cuando ocurre un error de base de datos</exception>
        public async Task<IReadOnlyList<Guid>> GetGlobalRewardsAsync(
            Guid promotionId, 
            CancellationToken cancellationToken = default)
        {
            if (promotionId == Guid.Empty)
            {
                throw new ArgumentException("El ID de promoción no puede ser vacío", nameof(promotionId));
            }

            try
            {
                _logger.LogDebug("Obteniendo recompensas globales para promoción {PromotionId}", promotionId);

                var rewardIds = await _dbContext.PromotionRewards
                    .AsNoTracking()
                    .Where(pr => pr.PromotionId == promotionId)
                    .Select(pr => pr.RewardId)
                    .ToListAsync(cancellationToken);

                _logger.LogDebug(
                    "Obtenidas {RewardCount} recompensas globales para promoción {PromotionId}",
                    rewardIds.Count, promotionId);

                return rewardIds.AsReadOnly();
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Operación cancelada para promoción {PromotionId}", promotionId);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Error obteniendo recompensas globales para promoción {PromotionId}",
                    promotionId);

                throw new InvalidOperationException(
                    $"Error al obtener recompensas globales para la promoción {promotionId}", ex);
            }
        }

        /// <summary>
        /// Obtiene los identificadores de las recompensas específicas asociadas a un grupo de expresiones.
        /// Estas son recompensas que solo pueden ser otorgadas cuando se cumple ese grupo específico.
        /// </summary>
        /// <param name="expressionGroupId">Identificador único del grupo de expresiones</param>
        /// <param name="cancellationToken">Token de cancelación</param>
        /// <returns>Lista de IDs de recompensas específicas del grupo</returns>
        /// <exception cref="ArgumentException">Cuando expressionGroupId es Guid.Empty</exception>
        /// <exception cref="InvalidOperationException">Cuando ocurre un error de base de datos</exception>
        public async Task<IReadOnlyList<Guid>> GetGroupRewardsAsync(
            Guid expressionGroupId, 
            CancellationToken cancellationToken = default)
        {
            if (expressionGroupId == Guid.Empty)
            {
                throw new ArgumentException("El ID del grupo de expresiones no puede ser vacío", nameof(expressionGroupId));
            }

            try
            {
                _logger.LogDebug(
                    "Obteniendo recompensas específicas para grupo {ExpressionGroupId}",
                    expressionGroupId);

                var rewardIds = await _dbContext.RuleGroupRewards
                    .AsNoTracking()
                    .Where(rgr => rgr.ExpressionGroupId == expressionGroupId)
                    .Select(rgr => rgr.RewardId)
                    .ToListAsync(cancellationToken);

                _logger.LogDebug(
                    "Obtenidas {RewardCount} recompensas específicas para grupo {ExpressionGroupId}",
                    rewardIds.Count, expressionGroupId);

                return rewardIds.AsReadOnly();
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Operación cancelada para grupo {ExpressionGroupId}", expressionGroupId);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Error obteniendo recompensas específicas para grupo {ExpressionGroupId}",
                    expressionGroupId);

                throw new InvalidOperationException(
                    $"Error al obtener recompensas específicas para el grupo {expressionGroupId}", ex);
            }
        }

        /// <summary>
        /// Obtiene todas las recompensas (globales y específicas) disponibles para una promoción
        /// </summary>
        /// <param name="promotionId">ID de la promoción</param>
        /// <param name="cancellationToken">Token de cancelación</param>
        /// <returns>Lista de todos los IDs de recompensas disponibles</returns>
        public async Task<IReadOnlyList<Guid>> GetAllAvailableRewardsAsync(
            Guid promotionId, 
            CancellationToken cancellationToken = default)
        {
            if (promotionId == Guid.Empty)
            {
                throw new ArgumentException("El ID de promoción no puede ser vacío", nameof(promotionId));
            }

            try
            {
                // Obtener recompensas globales
                var globalRewards = await GetGlobalRewardsAsync(promotionId, cancellationToken);

                // Obtener recompensas específicas de grupos
                var groupSpecificRewards = await _dbContext.RuleGroupRewards
                    .AsNoTracking()
                    .Join(_dbContext.ExpressionGroups.AsNoTracking(),
                          rgr => rgr.ExpressionGroupId,
                          eg => eg.Id,
                          (rgr, eg) => new { rgr.RewardId, eg.PromotionId })
                    .Where(joined => joined.PromotionId == promotionId)
                    .Select(joined => joined.RewardId)
                    .ToListAsync(cancellationToken);

                // Combinar y eliminar duplicados
                var allRewards = globalRewards.Union(groupSpecificRewards).ToList();

                _logger.LogDebug(
                    "Total de recompensas disponibles para promoción {PromotionId}: {TotalCount} (Globales: {GlobalCount}, Específicas: {SpecificCount})",
                    promotionId, allRewards.Count, globalRewards.Count, groupSpecificRewards.Count);

                return allRewards.AsReadOnly();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Error obteniendo todas las recompensas para promoción {PromotionId}",
                    promotionId);

                throw new InvalidOperationException(
                    $"Error al obtener todas las recompensas para la promoción {promotionId}", ex);
            }
        }
    }

    // ========================================
    // REPOSITORY: CONTACT REWARD
    // ========================================

    /// <summary>
    /// Repositorio para el acceso a datos de ContactReward.
    /// 
    /// Proporciona consultas optimizadas para el historial de recompensas otorgadas,
    /// incluyendo funcionalidades críticas como verificación de cooldowns,
    /// detección de duplicados e historial de otorgamientos.
    /// </summary>
    public sealed class ContactRewardRepository : IContactRewardRepository
    {
        private readonly PromoEngineDbContext _dbContext;
        private readonly ILogger<ContactRewardRepository> _logger;

        /// <summary>
        /// Inicializa una nueva instancia del repositorio de recompensas de contacto
        /// </summary>
        /// <param name="dbContext">Contexto de base de datos</param>
        /// <param name="logger">Logger para trazabilidad</param>
        /// <exception cref="ArgumentNullException">Cuando algún parámetro es nulo</exception>
        public ContactRewardRepository(
            PromoEngineDbContext dbContext, 
            ILogger<ContactRewardRepository> logger)
        {
            _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>
        /// Obtiene la última recompensa otorgada exitosamente a un contacto para una promoción específica.
        /// 
        /// Utiliza el índice ix_contact_reward_performance para optimización.
        /// Esta consulta es crítica para el cálculo de cooldowns.
        /// </summary>
        /// <param name="promotionId">Identificador de la promoción</param>
        /// <param name="contactId">Identificador del contacto</param>
        /// <param name="cancellationToken">Token de cancelación</param>
        /// <returns>Última recompensa otorgada o null si no existe</returns>
        /// <exception cref="ArgumentException">Cuando algún ID es Guid.Empty</exception>
        /// <exception cref="InvalidOperationException">Cuando ocurre un error de base de datos</exception>
        public async Task<ContactReward?> GetLastGrantedAsync(
            Guid promotionId, 
            Guid contactId, 
            CancellationToken cancellationToken = default)
        {
            ValidateContactRewardParameters(promotionId, contactId);

            try
            {
                _logger.LogDebug(
                    "Obteniendo última recompensa otorgada. Promoción: {PromotionId}, Contacto: {ContactId}",
                    promotionId, contactId);

                var lastReward = await _dbContext.ContactRewards
                    .AsNoTracking()
                    .Where(cr => cr.ContactId == contactId && 
                                cr.PromotionId == promotionId && 
                                cr.Status == RewardGrantStatus.Granted)
                    .OrderByDescending(cr => cr.GrantedAt)
                    .FirstOrDefaultAsync(cancellationToken);

                if (lastReward != null)
                {
                    _logger.LogDebug(
                        "Última recompensa encontrada: {RewardId}, Otorgada: {GrantedAt}, Tier: {TierLevel}",
                        lastReward.Id, lastReward.GrantedAt, lastReward.TierLevel);
                }
                else
                {
                    _logger.LogDebug(
                        "No se encontraron recompensas previas para contacto {ContactId} en promoción {PromotionId}",
                        contactId, promotionId);
                }

                return lastReward;
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Operación cancelada para contacto {ContactId}", contactId);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Error obteniendo última recompensa. Promoción: {PromotionId}, Contacto: {ContactId}",
                    promotionId, contactId);

                throw new InvalidOperationException(
                    $"Error al obtener última recompensa para contacto {contactId} en promoción {promotionId}", ex);
            }
        }

        /// <summary>
        /// Obtiene la última recompensa otorgada a un contacto para un tier específico de una promoción.
        /// 
        /// Esencial para calcular cooldowns específicos por tier cuando difieren del cooldown global.
        /// </summary>
        /// <param name="promotionId">Identificador de la promoción</param>
        /// <param name="contactId">Identificador del contacto</param>
        /// <param name="tierLevel">Nivel del tier específico</param>
        /// <param name="cancellationToken">Token de cancelación</param>
        /// <returns>Última recompensa del tier o null si no existe</returns>
        /// <exception cref="ArgumentException">Cuando algún parámetro es inválido</exception>
        /// <exception cref="InvalidOperationException">Cuando ocurre un error de base de datos</exception>
        public async Task<ContactReward?> GetLastGrantedForTierAsync(
            Guid promotionId, 
            Guid contactId, 
            int tierLevel, 
            CancellationToken cancellationToken = default)
        {
            ValidateContactRewardParameters(promotionId, contactId);

            if (tierLevel <= 0)
            {
                throw new ArgumentException("El nivel del tier debe ser mayor a cero", nameof(tierLevel));
            }

            try
            {
                _logger.LogDebug(
                    "Obteniendo última recompensa para tier. Promoción: {PromotionId}, Contacto: {ContactId}, Tier: {TierLevel}",
                    promotionId, contactId, tierLevel);

                var lastTierReward = await _dbContext.ContactRewards
                    .AsNoTracking()
                    .Where(cr => cr.ContactId == contactId && 
                                cr.PromotionId == promotionId && 
                                cr.TierLevel == tierLevel &&
                                cr.Status == RewardGrantStatus.Granted)
                    .OrderByDescending(cr => cr.GrantedAt)
                    .FirstOrDefaultAsync(cancellationToken);

                if (lastTierReward != null)
                {
                    _logger.LogDebug(
                        "Última recompensa de tier encontrada: {RewardId}, Otorgada: {GrantedAt}",
                        lastTierReward.Id, lastTierReward.GrantedAt);
                }
                else
                {
                    _logger.LogDebug(
                        "No se encontraron recompensas previas para tier {TierLevel}",
                        tierLevel);
                }

                return lastTierReward;
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Operación cancelada para tier {TierLevel}", tierLevel);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Error obteniendo última recompensa de tier. Promoción: {PromotionId}, Contacto: {ContactId}, Tier: {TierLevel}",
                    promotionId, contactId, tierLevel);

                throw new InvalidOperationException(
                    $"Error al obtener última recompensa del tier {tierLevel} para contacto {contactId}", ex);
            }
        }

        /// <summary>
        /// Verifica si ya existe una recompensa otorgada para un evento específico.
        /// 
        /// Utiliza el índice ix_contact_reward_idempotency para garantizar
        /// que no se otorguen recompensas duplicadas para el mismo evento fuente.
        /// </summary>
        /// <param name="contactId">Identificador del contacto</param>
        /// <param name="promotionId">Identificador de la promoción</param>
        /// <param name="eventId">Identificador del evento fuente</param>
        /// <param name="cancellationToken">Token de cancelación</param>
        /// <returns>True si ya existe una recompensa para el evento</returns>
        /// <exception cref="ArgumentException">Cuando algún parámetro es inválido</exception>
        /// <exception cref="InvalidOperationException">Cuando ocurre un error de base de datos</exception>
        public async Task<bool> ExistsForEventAsync(
            Guid contactId, 
            Guid promotionId, 
            string eventId, 
            CancellationToken cancellationToken = default)
        {
            ValidateContactRewardParameters(promotionId, contactId);

            if (string.IsNullOrWhiteSpace(eventId))
            {
                throw new ArgumentException("El ID del evento no puede estar vacío", nameof(eventId));
            }

            try
            {
                _logger.LogDebug(
                    "Verificando existencia para evento. Contacto: {ContactId}, Promoción: {PromotionId}, Evento: {EventId}",
                    contactId, promotionId, eventId);

                var exists = await _dbContext.ContactRewards
                    .AsNoTracking()
                    .AnyAsync(cr => cr.ContactId == contactId && 
                                   cr.PromotionId == promotionId && 
                                   cr.SourceEventId == eventId &&
                                   cr.Status == RewardGrantStatus.Granted, 
                             cancellationToken);

                _logger.LogDebug(
                    "Resultado verificación evento {EventId}: {Exists}",
                    eventId, exists);

                return exists;
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Operación cancelada para evento {EventId}", eventId);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Error verificando existencia de evento. Contacto: {ContactId}, Evento: {EventId}",
                    contactId, eventId);

                throw new InvalidOperationException(
                    $"Error al verificar existencia del evento {eventId} para contacto {contactId}", ex);
            }
        }

        /// <summary>
        /// Obtiene el historial completo de recompensas de un contacto para una promoción
        /// </summary>
        /// <param name="contactId">ID del contacto</param>
        /// <param name="promotionId">ID de la promoción</param>
        /// <param name="maxResults">Número máximo de resultados (opcional)</param>
        /// <param name="cancellationToken">Token de cancelación</param>
        /// <returns>Lista ordenada de recompensas (más recientes primero)</returns>
        public async Task<IReadOnlyList<ContactReward>> GetRewardHistoryAsync(
            Guid contactId,
            Guid promotionId,
            int? maxResults = null,
            CancellationToken cancellationToken = default)
        {
            ValidateContactRewardParameters(promotionId, contactId);

            try
            {
                IQueryable<ContactReward> query = _dbContext.ContactRewards
                    .AsNoTracking()
                    .Where(cr => cr.ContactId == contactId && cr.PromotionId == promotionId)
                    .OrderByDescending(cr => cr.GrantedAt);

                if (maxResults.HasValue && maxResults.Value > 0)
                {
                    query = query.Take(maxResults.Value);
                }

                var history = await query.ToListAsync(cancellationToken);

                _logger.LogDebug(
                    "Obtenido historial de {HistoryCount} recompensas para contacto {ContactId}",
                    history.Count, contactId);

                return history.AsReadOnly();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Error obteniendo historial de recompensas. Contacto: {ContactId}, Promoción: {PromotionId}",
                    contactId, promotionId);

                throw new InvalidOperationException(
                    $"Error al obtener historial de recompensas para contacto {contactId}", ex);
            }
        }

        /// <summary>
        /// Obtiene recompensas que salen del cooldown en una fecha específica
        /// </summary>
        /// <param name="targetDate">Fecha objetivo para verificar cooldown</param>
        /// <param name="cancellationToken">Token de cancelación</param>
        /// <returns>Lista de recompensas que salen del cooldown</returns>
        public async Task<IReadOnlyList<ContactReward>> GetRewardsExitingCooldownAsync(
            DateTimeOffset targetDate,
            CancellationToken cancellationToken = default)
        {
            try
            {
                var rewards = await _dbContext.ContactRewards
                    .AsNoTracking()
                    .Where(cr => cr.CooldownUntil.HasValue && 
                                cr.CooldownUntil.Value.Date == targetDate.Date &&
                                cr.Status == RewardGrantStatus.Granted)
                    .ToListAsync(cancellationToken);

                _logger.LogDebug(
                    "Encontradas {RewardCount} recompensas que salen del cooldown en {Date}",
                    rewards.Count, targetDate.Date);

                return rewards.AsReadOnly();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Error obteniendo recompensas que salen del cooldown para fecha {Date}",
                    targetDate);

                throw new InvalidOperationException(
                    $"Error al obtener recompensas que salen del cooldown para {targetDate:yyyy-MM-dd}", ex);
            }
        }

        /// <summary>
        /// Valida los parámetros comunes de contacto y promoción
        /// </summary>
        /// <param name="promotionId">ID de promoción</param>
        /// <param name="contactId">ID de contacto</param>
        /// <exception cref="ArgumentException">Cuando algún parámetro es inválido</exception>
        private static void ValidateContactRewardParameters(Guid promotionId, Guid contactId)
        {
            if (promotionId == Guid.Empty)
            {
                throw new ArgumentException("El ID de promoción no puede ser vacío", nameof(promotionId));
            }

            if (contactId == Guid.Empty)
            {
                throw new ArgumentException("El ID del contacto no puede ser vacío", nameof(contactId));
            }
        }
    }

    // ========================================
    // SERVICE: ATTRIBUTES
    // ========================================

    /// <summary>
    /// Servicio para el acceso a datos del catálogo de atributos y operadores.
    /// 
    /// Proporciona operaciones de solo lectura optimizadas para consultar metadatos
    /// del sistema utilizados en la construcción y validación de reglas de negocio.
    /// </summary>
    public sealed class AttributesService : IAttributesService
    {
        private readonly PromoEngineDbContext _dbContext;
        private readonly ILogger<AttributesService> _logger;

        /// <summary>
        /// Inicializa una nueva instancia del servicio de atributos
        /// </summary>
        /// <param name="dbContext">Contexto de base de datos</param>
        /// <param name="logger">Logger para trazabilidad</param>
        /// <exception cref="ArgumentNullException">Cuando algún parámetro es nulo</exception>
        public AttributesService(
            PromoEngineDbContext dbContext, 
            ILogger<AttributesService> logger)
        {
            _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>
        /// Obtiene todos los atributos disponibles en el catálogo del sistema.
        /// 
        /// Utiliza AsNoTracking() para optimizar consultas de solo lectura
        /// y evitar el overhead del change tracking de Entity Framework.
        /// </summary>
        /// <param name="cancellationToken">Token de cancelación</param>
        /// <returns>Lista completa de atributos del catálogo</returns>
        /// <exception cref="InvalidOperationException">Cuando ocurre un error de base de datos</exception>
        public async Task<IReadOnlyList<AttributeCatalog>> GetAttributesAsync(
            CancellationToken cancellationToken = default)
        {
            try
            {
                _logger.LogDebug("Obteniendo catálogo completo de atributos");

                var attributes = await _dbContext.AttributeCatalogs
                    .AsNoTracking()
                    .Where(attr => attr.IsExposed) // Solo atributos expuestos
                    .OrderBy(attr => attr.EntityLogicalName)
                    .ThenBy(attr => attr.CanonicalName)
                    .ToListAsync(cancellationToken);

                _logger.LogDebug(
                    "Obtenidos {AttributeCount} atributos del catálogo",
                    attributes.Count);

                return attributes.AsReadOnly();
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Operación de obtención de atributos cancelada");
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error obteniendo catálogo de atributos");

                throw new InvalidOperationException(
                    "Error al obtener el catálogo de atributos", ex);
            }
        }

        /// <summary>
        /// Obtiene todos los operadores disponibles en el catálogo del sistema.
        /// 
        /// Incluye solo operadores activos ordenados por código para presentación consistente.
        /// </summary>
        /// <param name="cancellationToken">Token de cancelación</param>
        /// <returns>Lista completa de operadores del catálogo</returns>
        /// <exception cref="InvalidOperationException">Cuando ocurre un error de base de datos</exception>
        public async Task<IReadOnlyList<OperatorCatalog>> GetOperatorsAsync(
            CancellationToken cancellationToken = default)
        {
            try
            {
                _logger.LogDebug("Obteniendo catálogo completo de operadores");

                var operators = await _dbContext.OperatorCatalogs
                    .AsNoTracking()
                    .Where(op => op.IsActive) // Solo operadores activos
                    .OrderBy(op => op.Code)
                    .ToListAsync(cancellationToken);

                _logger.LogDebug(
                    "Obtenidos {OperatorCount} operadores del catálogo",
                    operators.Count);

                return operators.AsReadOnly();
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Operación de obtención de operadores cancelada");
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error obteniendo catálogo de operadores");

                throw new InvalidOperationException(
                    "Error al obtener el catálogo de operadores", ex);
            }
        }

        /// <summary>
        /// Obtiene todas las combinaciones válidas de operador-tipo de datos soportadas.
        /// 
        /// Esta información es crítica para la validación de reglas durante la compilación
        /// y para la generación de interfaces de usuario dinámicas.
        /// </summary>
        /// <param name="cancellationToken">Token de cancelación</param>
        /// <returns>Lista de tipos soportados por operador</returns>
        /// <exception cref="InvalidOperationException">Cuando ocurre un error de base de datos</exception>
        public async Task<IReadOnlyList<OperatorSupportedType>> GetOperatorSupportedTypesAsync(
            CancellationToken cancellationToken = default)
        {
            try
            {
                _logger.LogDebug("Obteniendo tipos soportados por operadores");

                var supportedTypes = await _dbContext.OperatorSupportedTypes
                    .AsNoTracking()
                    .OrderBy(ost => ost.OperatorId)
                    .ThenBy(ost => ost.DataType)
                    .ToListAsync(cancellationToken);

                _logger.LogDebug(
                    "Obtenidas {SupportedTypeCount} combinaciones operador-tipo",
                    supportedTypes.Count);

                return supportedTypes.AsReadOnly();
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Operación de obtención de tipos soportados cancelada");
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error obteniendo tipos soportados por operadores");

                throw new InvalidOperationException(
                    "Error al obtener tipos soportados por operadores", ex);
            }
        }

        /// <summary>
        /// Obtiene atributos filtrados por entidad
        /// </summary>
        /// <param name="entityLogicalName">Nombre lógico de la entidad</param>
        /// <param name="cancellationToken">Token de cancelación</param>
        /// <returns>Lista de atributos de la entidad especificada</returns>
        public async Task<IReadOnlyList<AttributeCatalog>> GetAttributesByEntityAsync(
            string entityLogicalName,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(entityLogicalName))
            {
                throw new ArgumentException("El nombre de entidad no puede estar vacío", nameof(entityLogicalName));
            }

            try
            {
                var attributes = await _dbContext.AttributeCatalogs
                    .AsNoTracking()
                    .Where(attr => attr.EntityLogicalName == entityLogicalName.ToLowerInvariant() && 
                                  attr.IsExposed)
                    .OrderBy(attr => attr.CanonicalName)
                    .ToListAsync(cancellationToken);

                _logger.LogDebug(
                    "Obtenidos {AttributeCount} atributos para entidad {EntityName}",
                    attributes.Count, entityLogicalName);

                return attributes.AsReadOnly();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Error obteniendo atributos para entidad {EntityName}",
                    entityLogicalName);

                throw new InvalidOperationException(
                    $"Error al obtener atributos para la entidad {entityLogicalName}", ex);
            }
        }

        /// <summary>
        /// Obtiene operadores compatibles con un tipo de datos específico
        /// </summary>
        /// <param name="dataType">Tipo de datos</param>
        /// <param name="cancellationToken">Token de cancelación</param>
        /// <returns>Lista de operadores compatibles</returns>
        public async Task<IReadOnlyList<OperatorCatalog>> GetOperatorsByDataTypeAsync(
            DataType dataType,
            CancellationToken cancellationToken = default)
        {
            try
            {
                var operators = await _dbContext.OperatorCatalogs
                    .AsNoTracking()
                    .Where(op => op.IsActive && 
                                op.SupportedTypes.Any(st => st.DataType == dataType))
                    .OrderBy(op => op.Code)
                    .ToListAsync(cancellationToken);

                _logger.LogDebug(
                    "Obtenidos {OperatorCount} operadores compatibles con tipo {DataType}",
                    operators.Count, dataType);

                return operators.AsReadOnly();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Error obteniendo operadores para tipo {DataType}",
                    dataType);

                throw new InvalidOperationException(
                    $"Error al obtener operadores para el tipo {dataType}", ex);
            }
        }
    }
}