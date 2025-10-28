using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PromoEngine.Application;
using PromoEngine.Domain;
using StackExchange.Redis;

namespace PromoEngine.Infrastructure.Cache
{
    /// <summary>
    /// Implementación de cache distribuido usando Redis para almacenar promociones y workflows.
    /// 
    /// Este servicio actúa como adaptador entre la capa de aplicación y Redis, proporcionando
    /// operaciones de cache optimizadas para el motor de promociones. Implementa patrones de
    /// cache como warm-up, invalidación selectiva y estructuras de datos Redis especializadas.
    /// 
    /// Estructura de claves en Redis:
    /// - wf:{country}:{promotionId}:v{version} -> WorkflowJson
    /// - wf:manifest:{country}:{promotionId}:v{version} -> ManifestJson
    /// - wf:index:{country} -> SortedSet(promotionId, version)
    /// - wf:active:{country} -> Set(promotionId)
    /// - wf:metadata:{promotionId} -> Hash(nombre, timezone, cooldown, etc.)
    /// </summary>
    public sealed class PromotionCacheRedis : IPromotionCache
    {
        private readonly IConnectionMultiplexer _connectionMultiplexer;
        private readonly ILogger<PromotionCacheRedis> _logger;
        private readonly RedisCacheOptions _options;

        // Constantes para estructura de claves Redis
        private const string WorkflowKeyTemplate = "wf:{0}:{1}:v{2}";
        private const string ManifestKeyTemplate = "wf:manifest:{0}:{1}:v{2}";
        private const string IndexKeyTemplate = "wf:index:{0}";
        private const string ActiveSetKeyTemplate = "wf:active:{0}";
        private const string MetadataKeyTemplate = "wf:metadata:{0}";
        private const string CooldownKeyTemplate = "wf:cooldown:{0}:{1}";

        // Constantes para campos de metadatos
        private const string NameField = "name";
        private const string TimezoneField = "timezone";
        private const string GlobalCooldownField = "globalCooldown";
        private const string CreatedAtField = "createdAt";
        private const string LastUpdatedField = "lastUpdated";

        /// <summary>
        /// Inicializa una nueva instancia del cache Redis para promociones
        /// </summary>
        /// <param name="connectionMultiplexer">Multiplexor de conexiones Redis</param>
        /// <param name="logger">Logger para trazabilidad y diagnósticos</param>
        /// <param name="options">Opciones de configuración del cache Redis</param>
        /// <exception cref="ArgumentNullException">Cuando alguno de los parámetros es nulo</exception>
        public PromotionCacheRedis(
            IConnectionMultiplexer connectionMultiplexer,
            ILogger<PromotionCacheRedis> logger,
            IOptions<RedisCacheOptions> options)
        {
            _connectionMultiplexer = connectionMultiplexer ?? throw new ArgumentNullException(nameof(connectionMultiplexer));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        }

        /// <summary>
        /// Carga (warm-up) una promoción y su versión en el cache Redis.
        /// 
        /// Esta operación es crítica para el rendimiento del sistema ya que pre-carga
        /// los datos necesarios para la evaluación de reglas en tiempo de ejecución.
        /// Utiliza pipelines de Redis para optimizar las operaciones en lote.
        /// </summary>
        /// <param name="promotion">Promoción a cargar en cache</param>
        /// <param name="promotionVersion">Versión específica de la promoción</param>
        /// <param name="cancellationToken">Token de cancelación para operaciones asíncronas</param>
        /// <returns>Task que representa la operación asíncrona</returns>
        /// <exception cref="ArgumentNullException">Cuando la promoción o versión son nulas</exception>
        /// <exception cref="ArgumentException">Cuando la versión no pertenece a la promoción</exception>
        /// <exception cref="InvalidOperationException">Cuando ocurre un error de Redis</exception>
        public async Task WarmAsync(
            Promotion promotion,
            PromotionVersion promotionVersion,
            CancellationToken cancellationToken = default)
        {
            // Validación de parámetros de entrada
            ValidateWarmParameters(promotion, promotionVersion);

            _logger.LogInformation(
                "Iniciando warm-up de cache. PromotionId: {PromotionId}, Version: {Version}, Country: {Country}",
                promotion.Id, promotionVersion.Version, promotionVersion.CountryIso);

            try
            {
                var database = _connectionMultiplexer.GetDatabase(_options.DatabaseIndex);
                
                // Usar pipeline para optimizar las operaciones en lote
                var batch = database.CreateBatch();
                
                // Preparar todas las operaciones del warm-up
                var warmupOperations = PrepareWarmupOperations(batch, promotion, promotionVersion);

                // Ejecutar todas las operaciones en batch
                batch.Execute();

                // Esperar a que todas las operaciones se completen
                await Task.WhenAll(warmupOperations);

                _logger.LogInformation(
                    "Warm-up completado exitosamente. PromotionId: {PromotionId}, Operations: {OperationCount}",
                    promotion.Id, warmupOperations.Count);

                // Opcionalmente, verificar la integridad del cache
                if (_options.VerifyWarmupIntegrity)
                {
                    await VerifyWarmupIntegrityAsync(database, promotion, promotionVersion);
                }
            }
            catch (RedisException redisEx)
            {
                _logger.LogError(redisEx,
                    "Error de Redis durante warm-up. PromotionId: {PromotionId}, Version: {Version}",
                    promotion.Id, promotionVersion.Version);

                throw new InvalidOperationException(
                    $"Error de Redis al cargar promoción {promotion.Id} en cache", redisEx);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Error inesperado durante warm-up. PromotionId: {PromotionId}, Version: {Version}",
                    promotion.Id, promotionVersion.Version);

                throw new InvalidOperationException(
                    $"Error inesperado al cargar promoción {promotion.Id} en cache", ex);
            }
        }

        /// <summary>
        /// Obtiene el workflow JSON de una promoción específica desde el cache
        /// </summary>
        /// <param name="promotionId">Identificador de la promoción</param>
        /// <param name="countryIso">Código ISO del país</param>
        /// <param name="version">Versión específica (opcional, usa la más reciente si no se especifica)</param>
        /// <param name="cancellationToken">Token de cancelación</param>
        /// <returns>Workflow JSON o null si no se encuentra</returns>
        public async Task<string?> GetWorkflowAsync(
            Guid promotionId,
            string countryIso,
            int? version = null,
            CancellationToken cancellationToken = default)
        {
            ValidateGetParameters(promotionId, countryIso);

            try
            {
                var database = _connectionMultiplexer.GetDatabase(_options.DatabaseIndex);
                
                var effectiveVersion = version ?? await GetLatestVersionAsync(database, promotionId, countryIso);
                if (effectiveVersion == null)
                {
                    _logger.LogWarning(
                        "No se encontró versión para promoción. PromotionId: {PromotionId}, Country: {Country}",
                        promotionId, countryIso);
                    return null;
                }

                var workflowKey = GenerateWorkflowKey(countryIso, promotionId, effectiveVersion.Value);
                var workflowJson = await database.StringGetAsync(workflowKey);

                if (workflowJson.HasValue)
                {
                    _logger.LogDebug(
                        "Workflow encontrado en cache. PromotionId: {PromotionId}, Version: {Version}",
                        promotionId, effectiveVersion);
                }
                else
                {
                    _logger.LogWarning(
                        "Workflow no encontrado en cache. PromotionId: {PromotionId}, Version: {Version}",
                        promotionId, effectiveVersion);
                }

                return workflowJson;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Error obteniendo workflow desde cache. PromotionId: {PromotionId}, Country: {Country}",
                    promotionId, countryIso);
                
                throw new InvalidOperationException(
                    $"Error al obtener workflow para promoción {promotionId}", ex);
            }
        }

        /// <summary>
        /// Obtiene el manifiesto JSON de una promoción específica desde el cache
        /// </summary>
        /// <param name="promotionId">Identificador de la promoción</param>
        /// <param name="countryIso">Código ISO del país</param>
        /// <param name="version">Versión específica (opcional)</param>
        /// <param name="cancellationToken">Token de cancelación</param>
        /// <returns>Manifiesto JSON o null si no se encuentra</returns>
        public async Task<string?> GetManifestAsync(
            Guid promotionId,
            string countryIso,
            int? version = null,
            CancellationToken cancellationToken = default)
        {
            ValidateGetParameters(promotionId, countryIso);

            try
            {
                var database = _connectionMultiplexer.GetDatabase(_options.DatabaseIndex);
                
                var effectiveVersion = version ?? await GetLatestVersionAsync(database, promotionId, countryIso);
                if (effectiveVersion == null)
                {
                    return null;
                }

                var manifestKey = GenerateManifestKey(countryIso, promotionId, effectiveVersion.Value);
                var manifestJson = await database.StringGetAsync(manifestKey);

                return manifestJson;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Error obteniendo manifiesto desde cache. PromotionId: {PromotionId}, Country: {Country}",
                    promotionId, countryIso);
                
                throw new InvalidOperationException(
                    $"Error al obtener manifiesto para promoción {promotionId}", ex);
            }
        }

        /// <summary>
        /// Obtiene la lista de promociones activas para un país específico
        /// </summary>
        /// <param name="countryIso">Código ISO del país</param>
        /// <param name="cancellationToken">Token de cancelación</param>
        /// <returns>Lista de IDs de promociones activas</returns>
        public async Task<IReadOnlyList<Guid>> GetActivePromotionsAsync(
            string countryIso,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(countryIso))
                throw new ArgumentException("CountryIso no puede estar vacío", nameof(countryIso));

            try
            {
                var database = _connectionMultiplexer.GetDatabase(_options.DatabaseIndex);
                var activeSetKey = GenerateActiveSetKey(countryIso);
                
                var activePromotionIds = await database.SetMembersAsync(activeSetKey);
                
                var promotionGuids = activePromotionIds
                    .Where(id => id.HasValue && Guid.TryParse(id, out _))
                    .Select(id => Guid.Parse(id!))
                    .ToList()
                    .AsReadOnly();

                _logger.LogDebug(
                    "Obtenidas {Count} promociones activas para país {Country}",
                    promotionGuids.Count, countryIso);

                return promotionGuids;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Error obteniendo promociones activas. Country: {Country}",
                    countryIso);
                
                throw new InvalidOperationException(
                    $"Error al obtener promociones activas para país {countryIso}", ex);
            }
        }

        /// <summary>
        /// Invalida (elimina) una promoción específica del cache
        /// </summary>
        /// <param name="promotionId">Identificador de la promoción</param>
        /// <param name="countryIso">Código ISO del país (opcional, invalida todas las versiones si no se especifica)</param>
        /// <param name="cancellationToken">Token de cancelación</param>
        /// <returns>Task que representa la operación asíncrona</returns>
        public async Task InvalidateAsync(
            Guid promotionId,
            string? countryIso = null,
            CancellationToken cancellationToken = default)
        {
            if (promotionId == Guid.Empty)
                throw new ArgumentException("PromotionId no puede ser vacío", nameof(promotionId));

            try
            {
                var database = _connectionMultiplexer.GetDatabase(_options.DatabaseIndex);

                if (string.IsNullOrWhiteSpace(countryIso))
                {
                    // Invalidar para todos los países
                    await InvalidatePromotionGloballyAsync(database, promotionId);
                }
                else
                {
                    // Invalidar para país específico
                    await InvalidatePromotionForCountryAsync(database, promotionId, countryIso);
                }

                _logger.LogInformation(
                    "Promoción invalidada exitosamente. PromotionId: {PromotionId}, Country: {Country}",
                    promotionId, countryIso ?? "ALL");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Error invalidando promoción. PromotionId: {PromotionId}, Country: {Country}",
                    promotionId, countryIso);
                
                throw new InvalidOperationException(
                    $"Error al invalidar promoción {promotionId}", ex);
            }
        }

        /// <summary>
        /// Valida los parámetros del método WarmAsync
        /// </summary>
        private static void ValidateWarmParameters(Promotion promotion, PromotionVersion promotionVersion)
        {
            if (promotion == null)
                throw new ArgumentNullException(nameof(promotion));

            if (promotionVersion == null)
                throw new ArgumentNullException(nameof(promotionVersion));

            if (promotionVersion.PromotionId != promotion.Id)
                throw new ArgumentException(
                    "La versión de promoción no pertenece a la promoción especificada",
                    nameof(promotionVersion));

            if (string.IsNullOrWhiteSpace(promotionVersion.CountryIso))
                throw new ArgumentException(
                    "CountryIso de la versión no puede estar vacío",
                    nameof(promotionVersion));
        }

        /// <summary>
        /// Valida los parámetros de los métodos Get
        /// </summary>
        private static void ValidateGetParameters(Guid promotionId, string countryIso)
        {
            if (promotionId == Guid.Empty)
                throw new ArgumentException("PromotionId no puede ser vacío", nameof(promotionId));

            if (string.IsNullOrWhiteSpace(countryIso))
                throw new ArgumentException("CountryIso no puede estar vacío", nameof(countryIso));
        }

        /// <summary>
        /// Prepara todas las operaciones de warm-up en un batch de Redis
        /// </summary>
        /// <param name="batch">Batch de Redis para operaciones en lote</param>
        /// <param name="promotion">Promoción a cargar</param>
        /// <param name="promotionVersion">Versión de la promoción</param>
        /// <returns>Lista de tasks que representan las operaciones del batch</returns>
        private List<Task> PrepareWarmupOperations(
            IBatch batch,
            Promotion promotion,
            PromotionVersion promotionVersion)
        {
            var operations = new List<Task>();

            // Generar claves Redis
            var workflowKey = GenerateWorkflowKey(promotionVersion.CountryIso, promotion.Id, promotionVersion.Version);
            var manifestKey = GenerateManifestKey(promotionVersion.CountryIso, promotion.Id, promotionVersion.Version);
            var indexKey = GenerateIndexKey(promotionVersion.CountryIso);
            var activeSetKey = GenerateActiveSetKey(promotionVersion.CountryIso);
            var metadataKey = GenerateMetadataKey(promotion.Id);

            // 1. Almacenar workflow JSON
            var workflowOperation = batch.StringSetAsync(
                workflowKey, 
                promotionVersion.WorkflowJson, 
                _options.DefaultExpiry);
            operations.Add(workflowOperation);

            // 2. Almacenar manifiesto JSON
            var manifestOperation = batch.StringSetAsync(
                manifestKey, 
                promotionVersion.ManifestJson, 
                _options.DefaultExpiry);
            operations.Add(manifestOperation);

            // 3. Actualizar índice de versiones (sorted set)
            var indexOperation = batch.SortedSetAddAsync(
                indexKey, 
                promotion.Id.ToString(), 
                promotionVersion.Version);
            operations.Add(indexOperation);

            // 4. Marcar promoción como activa
            var activeOperation = batch.SetAddAsync(
                activeSetKey, 
                promotion.Id.ToString());
            operations.Add(activeOperation);

            // 5. Almacenar metadatos de la promoción
            var metadataOperation = StorePromotionMetadata(batch, metadataKey, promotion);
            operations.Add(metadataOperation);

            // 6. Configurar TTL para limpieza automática si está habilitado
            if (_options.EnableAutoExpiry)
            {
                var ttlOperation = batch.KeyExpireAsync(workflowKey, _options.DefaultExpiry);
                operations.Add(ttlOperation);
            }

            return operations;
        }

        /// <summary>
        /// Almacena los metadatos de la promoción como un hash Redis
        /// </summary>
        /// <param name="batch">Batch de Redis</param>
        /// <param name="metadataKey">Clave para los metadatos</param>
        /// <param name="promotion">Promoción con metadatos</param>
        /// <returns>Task de la operación</returns>
        private static Task StorePromotionMetadata(IBatch batch, string metadataKey, Promotion promotion)
        {
            var metadataFields = new HashEntry[]
            {
                new(NameField, promotion.Name),
                new(TimezoneField, promotion.Timezone),
                new(GlobalCooldownField, promotion.GlobalCooldownDays),
                new(CreatedAtField, promotion.CreatedAt.ToString("O", CultureInfo.InvariantCulture)),
                new(LastUpdatedField, DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture))
            };

            return batch.HashSetAsync(metadataKey, metadataFields);
        }

        /// <summary>
        /// Verifica la integridad del warm-up realizado
        /// </summary>
        /// <param name="database">Base de datos Redis</param>
        /// <param name="promotion">Promoción cargada</param>
        /// <param name="promotionVersion">Versión cargada</param>
        private async Task VerifyWarmupIntegrityAsync(
            IDatabase database,
            Promotion promotion,
            PromotionVersion promotionVersion)
        {
            try
            {
                var workflowKey = GenerateWorkflowKey(promotionVersion.CountryIso, promotion.Id, promotionVersion.Version);
                var manifestKey = GenerateManifestKey(promotionVersion.CountryIso, promotion.Id, promotionVersion.Version);

                var workflowExists = await database.KeyExistsAsync(workflowKey);
                var manifestExists = await database.KeyExistsAsync(manifestKey);

                if (!workflowExists || !manifestExists)
                {
                    _logger.LogWarning(
                        "Verificación de integridad falló. WorkflowExists: {WorkflowExists}, ManifestExists: {ManifestExists}",
                        workflowExists, manifestExists);
                }
                else
                {
                    _logger.LogDebug("Verificación de integridad exitosa para promoción {PromotionId}", promotion.Id);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error durante verificación de integridad para promoción {PromotionId}", promotion.Id);
            }
        }

        /// <summary>
        /// Obtiene la versión más reciente de una promoción para un país específico
        /// </summary>
        /// <param name="database">Base de datos Redis</param>
        /// <param name="promotionId">ID de la promoción</param>
        /// <param name="countryIso">Código ISO del país</param>
        /// <returns>Versión más reciente o null si no existe</returns>
        private async Task<int?> GetLatestVersionAsync(IDatabase database, Guid promotionId, string countryIso)
        {
            var indexKey = GenerateIndexKey(countryIso);
            var promotionScore = await database.SortedSetScoreAsync(indexKey, promotionId.ToString());
            
            return promotionScore.HasValue ? (int)promotionScore.Value : null;
        }

        /// <summary>
        /// Invalida una promoción globalmente (todos los países)
        /// </summary>
        private async Task InvalidatePromotionGloballyAsync(IDatabase database, Guid promotionId)
        {
            // Esta es una operación compleja que requeriría escanear todas las claves
            // Por simplicidad, implementamos invalidación por país conocido
            _logger.LogWarning(
                "Invalidación global no implementada completamente. PromotionId: {PromotionId}",
                promotionId);
            
            // Remover metadatos generales
            var metadataKey = GenerateMetadataKey(promotionId);
            await database.KeyDeleteAsync(metadataKey);
        }

        /// <summary>
        /// Invalida una promoción para un país específico
        /// </summary>
        private async Task InvalidatePromotionForCountryAsync(IDatabase database, Guid promotionId, string countryIso)
        {
            var batch = database.CreateBatch();
            var operations = new List<Task>();

            // Obtener versión actual para generar claves específicas
            var currentVersion = await GetLatestVersionAsync(database, promotionId, countryIso);
            
            if (currentVersion.HasValue)
            {
                var workflowKey = GenerateWorkflowKey(countryIso, promotionId, currentVersion.Value);
                var manifestKey = GenerateManifestKey(countryIso, promotionId, currentVersion.Value);
                
                operations.Add(batch.KeyDeleteAsync(workflowKey));
                operations.Add(batch.KeyDeleteAsync(manifestKey));
            }

            // Remover de índices
            var indexKey = GenerateIndexKey(countryIso);
            var activeSetKey = GenerateActiveSetKey(countryIso);
            
            operations.Add(batch.SortedSetRemoveAsync(indexKey, promotionId.ToString()));
            operations.Add(batch.SetRemoveAsync(activeSetKey, promotionId.ToString()));

            batch.Execute();
            await Task.WhenAll(operations);
        }

        // Métodos de generación de claves Redis
        private static string GenerateWorkflowKey(string countryIso, Guid promotionId, int version) =>
            string.Format(CultureInfo.InvariantCulture, WorkflowKeyTemplate, countryIso.ToUpperInvariant(), promotionId, version);

        private static string GenerateManifestKey(string countryIso, Guid promotionId, int version) =>
            string.Format(CultureInfo.InvariantCulture, ManifestKeyTemplate, countryIso.ToUpperInvariant(), promotionId, version);

        private static string GenerateIndexKey(string countryIso) =>
            string.Format(CultureInfo.InvariantCulture, IndexKeyTemplate, countryIso.ToUpperInvariant());

        private static string GenerateActiveSetKey(string countryIso) =>
            string.Format(CultureInfo.InvariantCulture, ActiveSetKeyTemplate, countryIso.ToUpperInvariant());

        private static string GenerateMetadataKey(Guid promotionId) =>
            string.Format(CultureInfo.InvariantCulture, MetadataKeyTemplate, promotionId);
    }

    /// <summary>
    /// Opciones de configuración para el cache Redis
    /// </summary>
    public sealed class RedisCacheOptions
    {
        /// <summary>
        /// Índice de base de datos Redis a utilizar (por defecto: 0)
        /// </summary>
        public int DatabaseIndex { get; set; } = 0;

        /// <summary>
        /// Tiempo de expiración por defecto para las claves (por defecto: 1 hora)
        /// </summary>
        public TimeSpan DefaultExpiry { get; set; } = TimeSpan.FromHours(1);

        /// <summary>
        /// Indica si se debe verificar la integridad después del warm-up (por defecto: false)
        /// </summary>
        public bool VerifyWarmupIntegrity { get; set; } = false;

        /// <summary>
        /// Indica si se debe habilitar expiración automática de claves (por defecto: true)
        /// </summary>
        public bool EnableAutoExpiry { get; set; } = true;

        /// <summary>
        /// Prefijo para todas las claves Redis (por defecto: vacío)
        /// </summary>
        public string KeyPrefix { get; set; } = string.Empty;

        /// <summary>
        /// Tamaño máximo del pipeline para operaciones en lote (por defecto: 100)
        /// </summary>
        public int MaxBatchSize { get; set; } = 100;
    }
}