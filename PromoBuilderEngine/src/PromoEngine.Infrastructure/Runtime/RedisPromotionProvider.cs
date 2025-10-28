using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PromoEngine.Application;
using PromoEngine.Domain;
using PromoEngine.Infrastructure.EF;
using StackExchange.Redis;

namespace PromoEngine.Infrastructure.Runtime
{
    /// <summary>
    /// Proveedor de promociones que utiliza Redis como cache primario con fallback a base de datos.
    /// 
    /// Implementa una estrategia híbrida de recuperación de datos donde Redis actúa como cache
    /// de alto rendimiento para promociones activas, con respaldo automático a Entity Framework
    /// cuando los datos no están disponibles en cache.
    /// 
    /// Arquitectura de datos:
    /// - Redis: Cache optimizado para consultas en tiempo real (runtime)
    /// - Database: Fuente autoritativa de datos con consultas complejas
    /// - Hybrid approach: Mejor rendimiento con consistencia garantizada
    /// </summary>
    public sealed class RedisPromotionProvider : IPromotionProvider
    {
        private readonly IConnectionMultiplexer _connectionMultiplexer;
        private readonly PromoEngineDbContext _dbContext;
        private readonly ILogger<RedisPromotionProvider> _logger;
        private readonly RedisPromotionProviderOptions _options;

        // Constantes para estructura de claves Redis
        private const string ActiveSetKeyTemplate = "wf:active:{0}";
        private const string IndexKeyTemplate = "wf:index:{0}";
        private const string WorkflowKeyTemplate = "wf:{0}:{1}:v{2}";
        private const string ManifestKeyTemplate = "wf:manifest:{0}:{1}:v{2}";

        // Constantes para parsing de manifiestos JSON
        private const string WindowProperty = "window";
        private const string ValidFromUtcProperty = "validFromUtc";
        private const string ValidToUtcProperty = "validToUtc";
        private const string PoliciesProperty = "policies";
        private const string GlobalCooldownDaysProperty = "globalCooldownDays";

        /// <summary>
        /// Inicializa una nueva instancia del proveedor de promociones Redis
        /// </summary>
        /// <param name="connectionMultiplexer">Multiplexor de conexiones Redis</param>
        /// <param name="dbContext">Contexto de base de datos Entity Framework</param>
        /// <param name="logger">Logger para trazabilidad y diagnósticos</param>
        /// <param name="options">Opciones de configuración del proveedor</param>
        /// <exception cref="ArgumentNullException">Cuando alguno de los parámetros es nulo</exception>
        public RedisPromotionProvider(
            IConnectionMultiplexer connectionMultiplexer,
            PromoEngineDbContext dbContext,
            ILogger<RedisPromotionProvider> logger,
            IOptions<RedisPromotionProviderOptions> options)
        {
            _connectionMultiplexer = connectionMultiplexer ?? throw new ArgumentNullException(nameof(connectionMultiplexer));
            _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        }

        /// <summary>
        /// Obtiene todas las promociones activas para un país específico en una fecha determinada.
        /// 
        /// Implementa una estrategia híbrida:
        /// 1. Intenta recuperar desde Redis (cache de alto rendimiento)
        /// 2. Si Redis no contiene datos, consulta la base de datos directamente
        /// 3. Aplica validaciones de ventana temporal en ambos casos
        /// </summary>
        /// <param name="countryIso">Código ISO del país (ej: ES, US, FR)</param>
        /// <param name="asOfUtc">Fecha y hora de evaluación en UTC</param>
        /// <param name="cancellationToken">Token de cancelación para operaciones asíncronas</param>
        /// <returns>Lista de promociones activas con sus versiones correspondientes</returns>
        /// <exception cref="ArgumentException">Cuando countryIso está vacío</exception>
        /// <exception cref="InvalidOperationException">Cuando ocurre un error en la recuperación de datos</exception>
        public async Task<IReadOnlyList<(Promotion p, PromotionVersion pv)>> GetActivePromotionsAsync(
            string countryIso,
            DateTimeOffset asOfUtc,
            CancellationToken cancellationToken = default)
        {
            ValidateGetActivePromotionsParameters(countryIso, asOfUtc);

            _logger.LogInformation(
                "Obteniendo promociones activas. País: {CountryIso}, Fecha: {AsOfUtc}",
                countryIso, asOfUtc);

            try
            {
                // Estrategia 1: Intentar recuperar desde Redis
                var promotionsFromRedis = await TryGetPromotionsFromRedisAsync(
                    countryIso, asOfUtc, cancellationToken);

                if (promotionsFromRedis.Count > 0)
                {
                    _logger.LogDebug(
                        "Recuperadas {Count} promociones desde Redis para país {CountryIso}",
                        promotionsFromRedis.Count, countryIso);

                    return promotionsFromRedis;
                }

                // Estrategia 2: Fallback a base de datos
                _logger.LogDebug(
                    "Redis no contiene datos, recuperando desde base de datos para país {CountryIso}",
                    countryIso);

                var promotionsFromDatabase = await GetPromotionsFromDatabaseAsync(
                    countryIso, asOfUtc, cancellationToken);

                _logger.LogInformation(
                    "Recuperación completada. Total promociones: {Count}, Fuente: {Source}, País: {CountryIso}",
                    promotionsFromDatabase.Count, 
                    promotionsFromRedis.Count > 0 ? "Redis" : "Database",
                    countryIso);

                return promotionsFromDatabase;
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation(
                    "Operación cancelada para país {CountryIso}",
                    countryIso);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Error obteniendo promociones activas. País: {CountryIso}, Fecha: {AsOfUtc}",
                    countryIso, asOfUtc);

                throw new InvalidOperationException(
                    $"Error al obtener promociones activas para {countryIso}", ex);
            }
        }

        /// <summary>
        /// Obtiene una promoción específica por ID y país desde el cache o base de datos
        /// </summary>
        /// <param name="promotionId">ID de la promoción</param>
        /// <param name="countryIso">Código ISO del país</param>
        /// <param name="asOfUtc">Fecha de evaluación</param>
        /// <param name="cancellationToken">Token de cancelación</param>
        /// <returns>Promoción y versión si está activa, null en caso contrario</returns>
        public async Task<(Promotion promotion, PromotionVersion version)?> GetActivePromotionByIdAsync(
            Guid promotionId,
            string countryIso,
            DateTimeOffset asOfUtc,
            CancellationToken cancellationToken = default)
        {
            if (promotionId == Guid.Empty)
                throw new ArgumentException("El ID de promoción no puede ser vacío", nameof(promotionId));

            ValidateGetActivePromotionsParameters(countryIso, asOfUtc);

            try
            {
                _logger.LogDebug(
                    "Buscando promoción específica. ID: {PromotionId}, País: {CountryIso}",
                    promotionId, countryIso);

                // Primero intentar desde Redis
                var promotionFromRedis = await TryGetSinglePromotionFromRedisAsync(
                    promotionId, countryIso, asOfUtc, cancellationToken);

                if (promotionFromRedis.HasValue)
                {
                    _logger.LogDebug("Promoción {PromotionId} encontrada en Redis", promotionId);
                    return promotionFromRedis;
                }

                // Fallback a base de datos
                var promotionFromDb = await GetSinglePromotionFromDatabaseAsync(
                    promotionId, countryIso, asOfUtc, cancellationToken);

                if (promotionFromDb.HasValue)
                {
                    _logger.LogDebug("Promoción {PromotionId} encontrada en base de datos", promotionId);
                }
                else
                {
                    _logger.LogDebug("Promoción {PromotionId} no encontrada o no activa", promotionId);
                }

                return promotionFromDb;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Error obteniendo promoción específica. ID: {PromotionId}, País: {CountryIso}",
                    promotionId, countryIso);

                throw new InvalidOperationException(
                    $"Error al obtener promoción {promotionId} para {countryIso}", ex);
            }
        }

        /// <summary>
        /// Valida los parámetros de entrada para GetActivePromotionsAsync
        /// </summary>
        /// <param name="countryIso">Código ISO del país</param>
        /// <param name="asOfUtc">Fecha de evaluación</param>
        /// <exception cref="ArgumentException">Cuando algún parámetro es inválido</exception>
        private static void ValidateGetActivePromotionsParameters(string countryIso, DateTimeOffset asOfUtc)
        {
            if (string.IsNullOrWhiteSpace(countryIso))
                throw new ArgumentException("CountryIso no puede estar vacío", nameof(countryIso));

            if (asOfUtc == default)
                throw new ArgumentException("AsOfUtc debe tener un valor válido", nameof(asOfUtc));
        }

        /// <summary>
        /// Intenta recuperar promociones desde Redis como cache primario
        /// </summary>
        /// <param name="countryIso">Código ISO del país</param>
        /// <param name="asOfUtc">Fecha de evaluación</param>
        /// <param name="cancellationToken">Token de cancelación</param>
        /// <returns>Lista de promociones desde Redis o lista vacía si no hay datos</returns>
        private async Task<IReadOnlyList<(Promotion promotion, PromotionVersion version)>> TryGetPromotionsFromRedisAsync(
            string countryIso,
            DateTimeOffset asOfUtc,
            CancellationToken cancellationToken)
        {
            try
            {
                var database = _connectionMultiplexer.GetDatabase(_options.DatabaseIndex);

                // Obtener IDs de promociones activas desde el conjunto Redis
                var activeSetKey = GenerateActiveSetKey(countryIso);
                var activePromotionIds = await database.SetMembersAsync(activeSetKey);

                if (!activePromotionIds.Any())
                {
                    _logger.LogDebug("No se encontraron promociones activas en Redis para {CountryIso}", countryIso);
                    return Array.Empty<(Promotion, PromotionVersion)>();
                }

                _logger.LogDebug(
                    "Encontradas {Count} promociones en conjunto activo de Redis para {CountryIso}",
                    activePromotionIds.Length, countryIso);

                var promotions = new List<(Promotion, PromotionVersion)>();

                // Procesar cada promoción activa
                foreach (var promotionIdValue in activePromotionIds)
                {
                    if (!Guid.TryParse(promotionIdValue, out var promotionId))
                    {
                        _logger.LogWarning("ID de promoción inválido en Redis: {InvalidId}", promotionIdValue);
                        continue;
                    }

                    var promotion = await ProcessSinglePromotionFromRedis(
                        promotionId, countryIso, asOfUtc, database, cancellationToken);

                    if (promotion.HasValue)
                    {
                        promotions.Add(promotion.Value);
                    }
                }

                return promotions.AsReadOnly();
            }
            catch (RedisException redisEx)
            {
                _logger.LogWarning(redisEx,
                    "Error de Redis recuperando promociones para {CountryIso}, fallback a base de datos",
                    countryIso);

                return Array.Empty<(Promotion, PromotionVersion)>();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Error inesperado recuperando desde Redis para {CountryIso}",
                    countryIso);

                return Array.Empty<(Promotion, PromotionVersion)>();
            }
        }

        /// <summary>
        /// Procesa una promoción individual desde Redis con validaciones completas
        /// </summary>
        /// <param name="promotionId">ID de la promoción</param>
        /// <param name="countryIso">Código ISO del país</param>
        /// <param name="asOfUtc">Fecha de evaluación</param>
        /// <param name="database">Base de datos Redis</param>
        /// <param name="cancellationToken">Token de cancelación</param>
        /// <returns>Promoción procesada o null si no es válida</returns>
        private async Task<(Promotion promotion, PromotionVersion version)?> ProcessSinglePromotionFromRedis(
            Guid promotionId,
            string countryIso,
            DateTimeOffset asOfUtc,
            IDatabase database,
            CancellationToken cancellationToken)
        {
            try
            {
                // Obtener versión actual desde índice ordenado
                var indexKey = GenerateIndexKey(countryIso);
                var version = (int?)await database.SortedSetScoreAsync(indexKey, promotionId.ToString());

                if (!version.HasValue || version.Value <= 0)
                {
                    _logger.LogDebug("Versión inválida para promoción {PromotionId}: {Version}", 
                        promotionId, version);
                    return null;
                }

                // Recuperar workflow y manifiesto desde Redis
                var workflowKey = GenerateWorkflowKey(countryIso, promotionId, version.Value);
                var manifestKey = GenerateManifestKey(countryIso, promotionId, version.Value);

                var workflowTask = database.StringGetAsync(workflowKey);
                var manifestTask = database.StringGetAsync(manifestKey);

                await Task.WhenAll(workflowTask, manifestTask);

                var workflowJson = await workflowTask;
                var manifestJson = await manifestTask;

                if (workflowJson.IsNullOrEmpty || manifestJson.IsNullOrEmpty)
                {
                    _logger.LogDebug(
                        "Datos incompletos en Redis para promoción {PromotionId}: Workflow={WorkflowExists}, Manifest={ManifestExists}",
                        promotionId, !workflowJson.IsNullOrEmpty, !manifestJson.IsNullOrEmpty);
                    return null;
                }

                // Validar ventana temporal desde manifiesto
                if (!IsPromotionActiveInTimeWindow(manifestJson!, asOfUtc))
                {
                    _logger.LogDebug(
                        "Promoción {PromotionId} fuera de ventana temporal para fecha {AsOfUtc}",
                        promotionId, asOfUtc);
                    return null;
                }

                // Obtener datos base de la promoción desde base de datos
                var basePromotion = await _dbContext.Promotions
                    .AsNoTracking()
                    .FirstOrDefaultAsync(p => p.Id == promotionId, cancellationToken);

                if (basePromotion == null)
                {
                    _logger.LogWarning("Promoción {PromotionId} no encontrada en base de datos", promotionId);
                    return null;
                }

                // Crear versión híbrida con datos de Redis y base de datos
                var promotionVersion = CreateHybridPromotionVersion(
                    promotionId, version.Value, countryIso, workflowJson!, manifestJson!, basePromotion);

                return (basePromotion, promotionVersion);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Error procesando promoción {PromotionId} desde Redis",
                    promotionId);

                return null;
            }
        }

        /// <summary>
        /// Intenta obtener una promoción específica desde Redis
        /// </summary>
        /// <param name="promotionId">ID de la promoción</param>
        /// <param name="countryIso">Código ISO del país</param>
        /// <param name="asOfUtc">Fecha de evaluación</param>
        /// <param name="cancellationToken">Token de cancelación</param>
        /// <returns>Promoción desde Redis o null si no existe</returns>
        private async Task<(Promotion promotion, PromotionVersion version)?> TryGetSinglePromotionFromRedisAsync(
            Guid promotionId,
            string countryIso,
            DateTimeOffset asOfUtc,
            CancellationToken cancellationToken)
        {
            try
            {
                var database = _connectionMultiplexer.GetDatabase(_options.DatabaseIndex);

                // Verificar si la promoción está en el conjunto activo
                var activeSetKey = GenerateActiveSetKey(countryIso);
                var isActive = await database.SetContainsAsync(activeSetKey, promotionId.ToString());

                if (!isActive)
                {
                    _logger.LogDebug("Promoción {PromotionId} no está en conjunto activo", promotionId);
                    return null;
                }

                return await ProcessSinglePromotionFromRedis(
                    promotionId, countryIso, asOfUtc, database, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Error obteniendo promoción específica desde Redis: {PromotionId}",
                    promotionId);

                return null;
            }
        }

        /// <summary>
        /// Recupera promociones activas directamente desde la base de datos como fallback
        /// </summary>
        /// <param name="countryIso">Código ISO del país</param>
        /// <param name="asOfUtc">Fecha de evaluación</param>
        /// <param name="cancellationToken">Token de cancelación</param>
        /// <returns>Lista de promociones activas desde base de datos</returns>
        private async Task<IReadOnlyList<(Promotion promotion, PromotionVersion version)>> GetPromotionsFromDatabaseAsync(
            string countryIso,
            DateTimeOffset asOfUtc,
            CancellationToken cancellationToken)
        {
            try
            {
                // Consulta optimizada con joins y filtros aplicados en base de datos
                var activePromotions = await (
                    from pv in _dbContext.PromotionVersions.AsNoTracking()
                    join p in _dbContext.Promotions.AsNoTracking() on pv.PromotionId equals p.Id
                    where pv.CountryIso == countryIso.ToUpperInvariant() &&
                          !pv.IsDraft &&
                          (pv.ValidityPeriod.ValidFromUtc == null || pv.ValidityPeriod.ValidFromUtc <= asOfUtc) &&
                          (pv.ValidityPeriod.ValidToUtc == null || pv.ValidityPeriod.ValidToUtc >= asOfUtc)
                    select new { Promotion = p, Version = pv }
                ).ToListAsync(cancellationToken);

                var result = activePromotions
                    .Select(x => (x.Promotion, x.Version))
                    .ToList()
                    .AsReadOnly();

                _logger.LogDebug(
                    "Recuperadas {Count} promociones desde base de datos para {CountryIso}",
                    result.Count, countryIso);

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Error recuperando promociones desde base de datos para {CountryIso}",
                    countryIso);

                throw new InvalidOperationException(
                    $"Error al recuperar promociones desde base de datos para {countryIso}", ex);
            }
        }

        /// <summary>
        /// Obtiene una promoción específica desde la base de datos
        /// </summary>
        /// <param name="promotionId">ID de la promoción</param>
        /// <param name="countryIso">Código ISO del país</param>
        /// <param name="asOfUtc">Fecha de evaluación</param>
        /// <param name="cancellationToken">Token de cancelación</param>
        /// <returns>Promoción desde base de datos o null si no existe</returns>
        private async Task<(Promotion promotion, PromotionVersion version)?> GetSinglePromotionFromDatabaseAsync(
            Guid promotionId,
            string countryIso,
            DateTimeOffset asOfUtc,
            CancellationToken cancellationToken)
        {
            try
            {
                var promotionData = await (
                    from pv in _dbContext.PromotionVersions.AsNoTracking()
                    join p in _dbContext.Promotions.AsNoTracking() on pv.PromotionId equals p.Id
                    where p.Id == promotionId &&
                          pv.CountryIso == countryIso.ToUpperInvariant() &&
                          !pv.IsDraft &&
                          (pv.ValidityPeriod.ValidFromUtc == null || pv.ValidityPeriod.ValidFromUtc <= asOfUtc) &&
                          (pv.ValidityPeriod.ValidToUtc == null || pv.ValidityPeriod.ValidToUtc >= asOfUtc)
                    select new { Promotion = p, Version = pv }
                ).FirstOrDefaultAsync(cancellationToken);

                return promotionData != null ? (promotionData.Promotion, promotionData.Version) : null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Error obteniendo promoción específica desde base de datos. ID: {PromotionId}",
                    promotionId);

                throw new InvalidOperationException(
                    $"Error al obtener promoción {promotionId} desde base de datos", ex);
            }
        }

        /// <summary>
        /// Valida si una promoción está activa dentro de su ventana temporal basándose en el manifiesto JSON
        /// </summary>
        /// <param name="manifestJson">Manifiesto JSON de la promoción</param>
        /// <param name="asOfUtc">Fecha de evaluación</param>
        /// <returns>True si la promoción está activa en la fecha especificada</returns>
        private bool IsPromotionActiveInTimeWindow(string manifestJson, DateTimeOffset asOfUtc)
        {
            try
            {
                using var document = JsonDocument.Parse(manifestJson);
                var root = document.RootElement;

                if (!root.TryGetProperty(WindowProperty, out var windowElement))
                {
                    // Si no hay ventana definida, se considera siempre activa
                    return true;
                }

                // Extraer fechas de validez desde el manifiesto
                var validFrom = ExtractDateTimeFromJson(windowElement, ValidFromUtcProperty);
                var validTo = ExtractDateTimeFromJson(windowElement, ValidToUtcProperty);

                // Validar ventana temporal
                if (validFrom.HasValue && validFrom.Value > asOfUtc)
                {
                    _logger.LogDebug(
                        "Promoción no activa: fecha actual {AsOfUtc} anterior a inicio {ValidFrom}",
                        asOfUtc, validFrom.Value);
                    return false;
                }

                if (validTo.HasValue && validTo.Value < asOfUtc)
                {
                    _logger.LogDebug(
                        "Promoción no activa: fecha actual {AsOfUtc} posterior a fin {ValidTo}",
                        asOfUtc, validTo.Value);
                    return false;
                }

                return true;
            }
            catch (JsonException jsonEx)
            {
                _logger.LogWarning(jsonEx, "Error parseando manifiesto JSON, asumiendo promoción activa");
                return true; // Fail-safe: asumir activa si no se puede parsear
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error validando ventana temporal, asumiendo promoción activa");
                return true; // Fail-safe: asumir activa ante errores
            }
        }

        /// <summary>
        /// Extrae un valor DateTimeOffset opcional desde un elemento JSON
        /// </summary>
        /// <param name="jsonElement">Elemento JSON contenedor</param>
        /// <param name="propertyName">Nombre de la propiedad a extraer</param>
        /// <returns>DateTimeOffset extraído o null si no existe o es null</returns>
        private DateTimeOffset? ExtractDateTimeFromJson(JsonElement jsonElement, string propertyName)
        {
            if (jsonElement.TryGetProperty(propertyName, out var dateElement) &&
                dateElement.ValueKind != JsonValueKind.Null)
            {
                try
                {
                    return dateElement.GetDateTimeOffset();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Error parseando fecha desde JSON para propiedad {PropertyName}",
                        propertyName);
                }
            }

            return null;
        }

        /// <summary>
        /// Lee el valor de cooldown global desde el manifiesto JSON de la promoción
        /// </summary>
        /// <param name="manifestJson">Manifiesto JSON</param>
        /// <returns>Días de cooldown global o null si no está definido</returns>
        private int? ExtractGlobalCooldownFromManifest(string manifestJson)
        {
            try
            {
                using var document = JsonDocument.Parse(manifestJson);
                var root = document.RootElement;

                if (root.TryGetProperty(PoliciesProperty, out var policiesElement) &&
                    policiesElement.TryGetProperty(GlobalCooldownDaysProperty, out var cooldownElement))
                {
                    return cooldownElement.GetInt32();
                }

                return null;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error extrayendo cooldown global desde manifiesto");
                return null;
            }
        }

        /// <summary>
        /// Crea una versión híbrida de promoción combinando datos de Redis y base de datos
        /// </summary>
        /// <param name="promotionId">ID de la promoción</param>
        /// <param name="version">Número de versión</param>
        /// <param name="countryIso">Código ISO del país</param>
        /// <param name="workflowJson">JSON del workflow desde Redis</param>
        /// <param name="manifestJson">JSON del manifiesto desde Redis</param>
        /// <param name="basePromotion">Datos base desde base de datos</param>
        /// <returns>Versión híbrida de la promoción</returns>
        private PromotionVersion CreateHybridPromotionVersion(
            Guid promotionId,
            int version,
            string countryIso,
            string workflowJson,
            string manifestJson,
            Promotion basePromotion)
        {
            var manifestCooldown = ExtractGlobalCooldownFromManifest(manifestJson);
            var effectiveCooldown = manifestCooldown ?? basePromotion.GlobalCooldownDays;

            // Crear la versión usando el factory method del dominio
            var validityPeriod = ExtractValidityPeriodFromManifest(manifestJson);
            
            return PromotionVersion.Create(
                promotionId: promotionId,
                version: version,
                countryIso: countryIso,
                manifestJson: manifestJson,
                workflowJson: workflowJson,
                timezone: basePromotion.Timezone,
                globalCooldownDays: effectiveCooldown,
                validFromUtc: validityPeriod.ValidFromUtc,
                validToUtc: validityPeriod.ValidToUtc);
        }

        /// <summary>
        /// Extrae el período de validez desde el manifiesto JSON
        /// </summary>
        /// <param name="manifestJson">JSON del manifiesto</param>
        /// <returns>Período de validez extraído</returns>
        private ValidityPeriod ExtractValidityPeriodFromManifest(string manifestJson)
        {
            try
            {
                using var document = JsonDocument.Parse(manifestJson);
                var root = document.RootElement;

                if (root.TryGetProperty(WindowProperty, out var windowElement))
                {
                    var validFrom = ExtractDateTimeFromJson(windowElement, ValidFromUtcProperty);
                    var validTo = ExtractDateTimeFromJson(windowElement, ValidToUtcProperty);

                    return ValidityPeriod.Create(validFrom, validTo);
                }

                return ValidityPeriod.Create(null, null);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error extrayendo período de validez, usando valores por defecto");
                return ValidityPeriod.Create(null, null);
            }
        }

        // Métodos de generación de claves Redis
        private static string GenerateActiveSetKey(string countryIso) =>
            string.Format(ActiveSetKeyTemplate, countryIso.ToUpperInvariant());

        private static string GenerateIndexKey(string countryIso) =>
            string.Format(IndexKeyTemplate, countryIso.ToUpperInvariant());

        private static string GenerateWorkflowKey(string countryIso, Guid promotionId, int version) =>
            string.Format(WorkflowKeyTemplate, countryIso.ToUpperInvariant(), promotionId, version);

        private static string GenerateManifestKey(string countryIso, Guid promotionId, int version) =>
            string.Format(ManifestKeyTemplate, countryIso.ToUpperInvariant(), promotionId, version);
    }

    /// <summary>
    /// Opciones de configuración para el RedisPromotionProvider
    /// </summary>
    public sealed class RedisPromotionProviderOptions
    {
        /// <summary>
        /// Índice de base de datos Redis a utilizar (por defecto: 0)
        /// </summary>
        public int DatabaseIndex { get; set; } = 0;

        /// <summary>
        /// Timeout para operaciones Redis en milisegundos (por defecto: 5000)
        /// </summary>
        public int TimeoutMilliseconds { get; set; } = 5000;

        /// <summary>
        /// Indica si se debe usar compresión para datos JSON (por defecto: false)
        /// </summary>
        public bool UseCompression { get; set; } = false;

        /// <summary>
        /// Prefijo para claves Redis (por defecto: vacío)
        /// </summary>
        public string KeyPrefix { get; set; } = string.Empty;

        /// <summary>
        /// Indica si se debe hacer fallback a base de datos cuando Redis falla (por defecto: true)
        /// </summary>
        public bool EnableDatabaseFallback { get; set; } = true;

        /// <summary>
        /// Número máximo de promociones a recuperar en una consulta (por defecto: 100)
        /// </summary>
        public int MaxPromotionsPerQuery { get; set; } = 100;
    }
}