using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PromoEngine.Application;
using RulesEngine;
using RulesEngine.Models;

namespace PromoEngine.Infrastructure.Runtime
{
    /// <summary>
    /// Servicio de motor de reglas que proporciona evaluación de reglas de negocio
    /// con cache optimizado y observabilidad completa.
    /// 
    /// Implementa una capa de abstracción sobre RulesEngine que incluye:
    /// - Cache inteligente de workflows compilados para optimizar rendimiento
    /// - Validación robusta de parámetros de entrada y reglas
    /// - Logging estructurado para trazabilidad y diagnóstico
    /// - Manejo de errores con contexto detallado
    /// - Métricas de rendimiento y uso
    /// 
    /// Arquitectura:
    /// - Domain Service: Implementa lógica de negocio de evaluación de reglas
    /// - Infrastructure Adapter: Abstrae la complejidad del RulesEngine externo
    /// - Performance Optimized: Cache y reutilización de motores compilados
    /// </summary>
    public sealed class RuleEngineService : IRuleEngineService, IDisposable
    {
        private readonly IMemoryCache _workflowCache;
        private readonly ILogger<RuleEngineService> _logger;
        private readonly RuleEngineServiceOptions _options;
        
        // Cache thread-safe de engines compilados para reutilización
        private readonly ConcurrentDictionary<string, RulesEngine.RulesEngine> _engineCache;
        
        // Métricas para monitoreo y optimización
        private readonly ConcurrentDictionary<string, RuleExecutionMetrics> _executionMetrics;
        
        private bool _disposed;

        // Constantes para configuración y logging
        private const string DefaultWorkflowName = "DefaultWorkflow";
        private const string ContextParameterName = "ctx";
        private const int DefaultMaxCachedEngines = 100;
        private const int DefaultCacheExpirationMinutes = 30;
        private const int MaxCachedEngines = 100;

        /// <summary>
        /// Inicializa una nueva instancia del servicio de motor de reglas
        /// </summary>
        /// <param name="memoryCache">Cache en memoria para workflows y engines compilados</param>
        /// <param name="logger">Logger para observabilidad y diagnósticos</param>
        /// <param name="options">Opciones de configuración del servicio</param>
        /// <exception cref="ArgumentNullException">Cuando alguno de los parámetros requeridos es nulo</exception>
        public RuleEngineService(
            IMemoryCache memoryCache,
            ILogger<RuleEngineService> logger,
            IOptions<RuleEngineServiceOptions> options)
        {
            _workflowCache = memoryCache ?? throw new ArgumentNullException(nameof(memoryCache));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
            
            _engineCache = new ConcurrentDictionary<string, RulesEngine.RulesEngine>();
            _executionMetrics = new ConcurrentDictionary<string, RuleExecutionMetrics>();

            _logger.LogInformation(
                "RuleEngineService inicializado con configuración: CacheExpiration={CacheExpiration}min, MaxEngines={MaxEngines}",
                _options.CacheExpirationMinutes, _options.MaxCachedEngines);
        }

        /// <summary>
        /// Evalúa una regla específica dentro de un workflow usando el contexto proporcionado.
        /// 
        /// Proceso de evaluación:
        /// 1. Validación de parámetros de entrada
        /// 2. Recuperación o creación del engine compilado (con cache)
        /// 3. Preparación del contexto de ejecución
        /// 4. Ejecución de la regla con timeout y métricas
        /// 5. Procesamiento del resultado con logging detallado
        /// </summary>
        /// <param name="workflow">Workflow que contiene las reglas a evaluar</param>
        /// <param name="ruleName">Nombre específico de la regla a ejecutar</param>
        /// <param name="context">Objeto de contexto con datos para la evaluación</param>
        /// <param name="cancellationToken">Token de cancelación para operaciones asíncronas</param>
        /// <returns>True si la regla se evalúa exitosamente, False en caso contrario</returns>
        /// <exception cref="ArgumentNullException">Cuando algún parámetro requerido es nulo</exception>
        /// <exception cref="ArgumentException">Cuando los parámetros tienen valores inválidos</exception>
        /// <exception cref="RuleEvaluationException">Cuando ocurre un error durante la evaluación</exception>
        public async Task<bool> EvaluateAsync(
            Workflow workflow,
            string ruleName,
            object context,
            CancellationToken cancellationToken = default)
        {
            ValidateEvaluationParameters(workflow, ruleName, context);

            var executionId = Guid.NewGuid().ToString("N")[..8];
            var stopwatch = Stopwatch.StartNew();

            _logger.LogDebug(
                "Iniciando evaluación de regla. ExecutionId: {ExecutionId}, Workflow: {WorkflowName}, Rule: {RuleName}",
                executionId, workflow.WorkflowName, ruleName);

            try
            {
                // Obtener engine compilado (con cache para optimización)
                var engine = await GetOrCreateRulesEngineAsync(workflow, cancellationToken);

                // Preparar contexto de ejecución
                var ruleParameters = CreateRuleParameters(context);

                // Ejecutar regla con timeout configurado
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(TimeSpan.FromMilliseconds(_options.EvaluationTimeoutMs));

                var result = await ExecuteRuleWithTimeoutAsync(
                    engine, workflow.WorkflowName, ruleName, ruleParameters, timeoutCts.Token);

                stopwatch.Stop();

                // Registrar métricas de ejecución
                await RecordExecutionMetricsAsync(workflow.WorkflowName, ruleName, stopwatch.Elapsed, result.IsSuccess);

                // Logging del resultado
                LogEvaluationResult(executionId, workflow.WorkflowName, ruleName, result, stopwatch.Elapsed);

                return result.IsSuccess;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                stopwatch.Stop();
                _logger.LogInformation(
                    "Evaluación cancelada por usuario. ExecutionId: {ExecutionId}, Duration: {Duration}ms",
                    executionId, stopwatch.ElapsedMilliseconds);
                
                throw;
            }
            catch (OperationCanceledException)
            {
                stopwatch.Stop();
                _logger.LogWarning(
                    "Evaluación cancelada por timeout. ExecutionId: {ExecutionId}, Duration: {Duration}ms, Timeout: {Timeout}ms",
                    executionId, stopwatch.ElapsedMilliseconds, _options.EvaluationTimeoutMs);
                
                throw new RuleEvaluationException(
                    $"La evaluación de la regla '{ruleName}' excedió el timeout de {_options.EvaluationTimeoutMs}ms",
                    new TimeoutException());
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                
                await RecordExecutionMetricsAsync(workflow.WorkflowName, ruleName, stopwatch.Elapsed, false);
                
                _logger.LogError(ex,
                    "Error evaluando regla. ExecutionId: {ExecutionId}, Workflow: {WorkflowName}, Rule: {RuleName}, Duration: {Duration}ms",
                    executionId, workflow.WorkflowName, ruleName, stopwatch.ElapsedMilliseconds);

                throw new RuleEvaluationException(
                    $"Error evaluando regla '{ruleName}' en workflow '{workflow.WorkflowName}'", ex);
            }
        }

        /// <summary>
        /// Evalúa múltiples reglas de un workflow en paralelo para optimizar rendimiento
        /// </summary>
        /// <param name="workflow">Workflow que contiene las reglas</param>
        /// <param name="ruleNames">Nombres de las reglas a evaluar</param>
        /// <param name="context">Contexto compartido para todas las evaluaciones</param>
        /// <param name="cancellationToken">Token de cancelación</param>
        /// <returns>Diccionario con resultados por nombre de regla</returns>
        public async Task<IReadOnlyDictionary<string, bool>> EvaluateMultipleAsync(
            Workflow workflow,
            IEnumerable<string> ruleNames,
            object context,
            CancellationToken cancellationToken = default)
        {
            ValidateEvaluationParameters(workflow, string.Empty, context);
            
            if (ruleNames == null)
                throw new ArgumentNullException(nameof(ruleNames));

            var ruleNamesList = ruleNames.ToList();
            if (!ruleNamesList.Any())
                return new Dictionary<string, bool>();

            _logger.LogDebug(
                "Iniciando evaluación múltiple. Workflow: {WorkflowName}, Rules: {RuleCount}",
                workflow.WorkflowName, ruleNamesList.Count);

            var evaluationTasks = ruleNamesList.Select(async ruleName =>
            {
                try
                {
                    var result = await EvaluateAsync(workflow, ruleName, context, cancellationToken);
                    return new KeyValuePair<string, bool>(ruleName, result);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "Error en evaluación múltiple para regla {RuleName}",
                        ruleName);
                    
                    // En evaluación múltiple, registrar el error pero continuar con otras reglas
                    return new KeyValuePair<string, bool>(ruleName, false);
                }
            });

            var results = await Task.WhenAll(evaluationTasks);
            
            var resultDictionary = results.ToDictionary(r => r.Key, r => r.Value);

            _logger.LogInformation(
                "Evaluación múltiple completada. Workflow: {WorkflowName}, Total: {Total}, Exitosas: {Successful}",
                workflow.WorkflowName, 
                resultDictionary.Count,
                resultDictionary.Count(r => r.Value));

            return resultDictionary;
        }

        /// <summary>
        /// Obtiene las métricas de ejecución acumuladas para análisis de rendimiento
        /// </summary>
        /// <returns>Diccionario con métricas por workflow</returns>
        public IReadOnlyDictionary<string, RuleExecutionMetrics> GetExecutionMetrics()
        {
            return _executionMetrics.ToDictionary(
                kvp => kvp.Key, 
                kvp => kvp.Value.Clone());
        }

        /// <summary>
        /// Limpia el cache de engines y métricas para liberar memoria
        /// </summary>
        /// <param name="includeMetrics">Si se deben limpiar también las métricas</param>
        public void ClearCache(bool includeMetrics = false)
        {
            _logger.LogInformation(
                "Limpiando cache. Engines: {EngineCount}, Métricas: {ClearMetrics}",
                _engineCache.Count, includeMetrics);

            // Limpiar engines compilados
            foreach (var engine in _engineCache.Values)
            {
                try
                {
                    // RulesEngine 6.0.0 no implementa IDisposable
                    // engine?.Dispose();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error liberando engine del cache");
                }
            }
            
            _engineCache.Clear();

            // Limpiar métricas si se solicita
            if (includeMetrics)
            {
                _executionMetrics.Clear();
            }

            _logger.LogInformation("Cache limpiado exitosamente");
        }

        /// <summary>
        /// Valida los parámetros de entrada para la evaluación de reglas
        /// </summary>
        /// <param name="workflow">Workflow a validar</param>
        /// <param name="ruleName">Nombre de la regla a validar</param>
        /// <param name="context">Contexto a validar</param>
        /// <exception cref="ArgumentNullException">Cuando algún parámetro requerido es nulo</exception>
        /// <exception cref="ArgumentException">Cuando algún parámetro tiene valor inválido</exception>
        private static void ValidateEvaluationParameters(Workflow workflow, string ruleName, object context)
        {
            if (workflow == null)
                throw new ArgumentNullException(nameof(workflow));

            if (context == null)
                throw new ArgumentNullException(nameof(context));

            if (!string.IsNullOrEmpty(ruleName) && string.IsNullOrWhiteSpace(ruleName))
                throw new ArgumentException("El nombre de la regla no puede estar vacío", nameof(ruleName));

            if (string.IsNullOrWhiteSpace(workflow.WorkflowName))
                throw new ArgumentException("El workflow debe tener un nombre válido", nameof(workflow));

            if (workflow.Rules == null || !workflow.Rules.Any())
                throw new ArgumentException("El workflow debe contener al menos una regla", nameof(workflow));
        }

        /// <summary>
        /// Obtiene o crea un engine de reglas compilado, utilizando cache para optimizar rendimiento
        /// </summary>
        /// <param name="workflow">Workflow para compilar</param>
        /// <param name="cancellationToken">Token de cancelación</param>
        /// <returns>Engine de reglas compilado y listo para usar</returns>
        private async Task<RulesEngine.RulesEngine> GetOrCreateRulesEngineAsync(
            Workflow workflow,
            CancellationToken cancellationToken)
        {
            var workflowKey = GenerateWorkflowCacheKey(workflow);

            // Intentar obtener desde cache en memoria
            if (_engineCache.TryGetValue(workflowKey, out var cachedEngine))
            {
                _logger.LogDebug("Engine recuperado desde cache para workflow {WorkflowName}", workflow.WorkflowName);
                return cachedEngine;
            }

            // Crear nuevo engine si no está en cache
            _logger.LogDebug("Creando nuevo engine para workflow {WorkflowName}", workflow.WorkflowName);

            var engine = await CreateRulesEngineAsync(workflow, cancellationToken);

            // Gestionar el tamaño del cache
            if (_engineCache.Count >= _options.MaxCachedEngines)
            {
                await EvictOldestCacheEntriesAsync();
            }

            // Agregar al cache
            _engineCache.TryAdd(workflowKey, engine);

            _logger.LogDebug(
                "Engine creado y cacheado para workflow {WorkflowName}. Cache size: {CacheSize}",
                workflow.WorkflowName, _engineCache.Count);

            return engine;
        }

        /// <summary>
        /// Crea un nuevo engine de reglas con configuración optimizada
        /// </summary>
        /// <param name="workflow">Workflow a compilar</param>
        /// <param name="cancellationToken">Token de cancelación</param>
        /// <returns>Engine compilado</returns>
        private async Task<RulesEngine.RulesEngine> CreateRulesEngineAsync(
            Workflow workflow,
            CancellationToken cancellationToken)
        {
            return await Task.Run(() =>
            {
                var settings = new ReSettings
                {
                    CustomTypes = _options.CustomTypes?.ToArray(),
                    IgnoreException = _options.IgnoreExceptions
                    // EnableExpressionDebug no existe en RulesEngine 6.0.0
                };

                var engine = new RulesEngine.RulesEngine(new[] { workflow }, settings);
                
                _logger.LogDebug(
                    "Engine creado para workflow {WorkflowName} con {RuleCount} reglas",
                    workflow.WorkflowName, workflow.Rules?.Count() ?? 0);

                return engine;
            }, cancellationToken);
        }

        /// <summary>
        /// Ejecuta una regla con timeout y manejo de excepciones
        /// </summary>
        /// <param name="engine">Engine de reglas</param>
        /// <param name="workflowName">Nombre del workflow</param>
        /// <param name="ruleName">Nombre de la regla</param>
        /// <param name="parameters">Parámetros de ejecución</param>
        /// <param name="cancellationToken">Token de cancelación con timeout</param>
        /// <returns>Resultado de la ejecución</returns>
        private async Task<RuleResultTree> ExecuteRuleWithTimeoutAsync(
            RulesEngine.RulesEngine engine,
            string workflowName,
            string ruleName,
            RuleParameter[] parameters,
            CancellationToken cancellationToken)
        {
            return await Task.Run(async () =>
            {
                try
                {
                    // En RulesEngine 6.0.0 se usa ExecuteAllRulesAsync
                    var results = await engine.ExecuteAllRulesAsync(workflowName, parameters);
                    var result = results.FirstOrDefault(r => r.Rule.RuleName == ruleName);
                    
                    if (result == null)
                    {
                        throw new RuleEvaluationException($"Regla '{ruleName}' no encontrada en el resultado");
                    }
                    return result;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "Error en ejecución de engine para regla {RuleName} en workflow {WorkflowName}",
                        ruleName, workflowName);
                    
                    throw new RuleEvaluationException(
                        $"Error ejecutando regla '{ruleName}': {ex.Message}", ex);
                }
            }, cancellationToken);
        }

        /// <summary>
        /// Crea los parámetros de regla necesarios para la ejecución
        /// </summary>
        /// <param name="context">Contexto de datos</param>
        /// <returns>Array de parámetros para el engine</returns>
        private RuleParameter[] CreateRuleParameters(object context)
        {
            var parameters = new List<RuleParameter>
            {
                new(ContextParameterName, context)
            };

            // Agregar parámetros adicionales si están configurados
            if (_options.AdditionalParameters?.Any() == true)
            {
                parameters.AddRange(_options.AdditionalParameters.Select(p => 
                    new RuleParameter(p.Key, p.Value)));
            }

            _logger.LogTrace(
                "Parámetros de regla creados: {ParameterCount} parámetros",
                parameters.Count);

            return parameters.ToArray();
        }

        /// <summary>
        /// Registra métricas de ejecución para análisis de rendimiento
        /// </summary>
        /// <param name="workflowName">Nombre del workflow</param>
        /// <param name="ruleName">Nombre de la regla</param>
        /// <param name="executionTime">Tiempo de ejecución</param>
        /// <param name="success">Indica si la ejecución fue exitosa</param>
        private async Task RecordExecutionMetricsAsync(
            string workflowName,
            string ruleName,
            TimeSpan executionTime,
            bool success)
        {
            await Task.Run(() =>
            {
                var metricsKey = $"{workflowName}:{ruleName}";
                
                _executionMetrics.AddOrUpdate(metricsKey,
                    new RuleExecutionMetrics(ruleName, workflowName),
                    (key, existing) =>
                    {
                        existing.RecordExecution(executionTime, success);
                        return existing;
                    });
            });
        }

        /// <summary>
        /// Registra el resultado de la evaluación con el nivel de logging apropiado
        /// </summary>
        /// <param name="executionId">ID de ejecución</param>
        /// <param name="workflowName">Nombre del workflow</param>
        /// <param name="ruleName">Nombre de la regla</param>
        /// <param name="result">Resultado de la evaluación</param>
        /// <param name="executionTime">Tiempo de ejecución</param>
        private void LogEvaluationResult(
            string executionId,
            string workflowName,
            string ruleName,
            RuleResultTree result,
            TimeSpan executionTime)
        {
            var logLevel = result.IsSuccess ? LogLevel.Debug : LogLevel.Information;

            _logger.Log(logLevel,
                "Evaluación completada. ExecutionId: {ExecutionId}, Workflow: {WorkflowName}, " +
                "Rule: {RuleName}, Success: {Success}, Duration: {Duration}ms, " +
                "ErrorMessage: {ErrorMessage}",
                executionId, workflowName, ruleName, result.IsSuccess, 
                executionTime.TotalMilliseconds, result.ExceptionMessage);

            // Log adicional para errores con stack trace si está disponible
            if (!result.IsSuccess && !string.IsNullOrEmpty(result.ExceptionMessage))
            {
                _logger.LogWarning(
                    "Detalles del error en regla {RuleName}: {ErrorDetails}",
                    ruleName, result.ExceptionMessage);
            }
        }

        /// <summary>
        /// Genera una clave única para el cache basada en el contenido del workflow
        /// </summary>
        /// <param name="workflow">Workflow para generar la clave</param>
        /// <returns>Clave única para cache</returns>
        private string GenerateWorkflowCacheKey(Workflow workflow)
        {
            // Generar hash basado en contenido del workflow para invalidación automática
            var workflowJson = JsonSerializer.Serialize(workflow, new JsonSerializerOptions
            {
                WriteIndented = false,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });

            var hash = workflowJson.GetHashCode();
            return $"workflow:{workflow.WorkflowName}:hash:{hash}";
        }

        /// <summary>
        /// Elimina las entradas más antiguas del cache cuando se alcanza el límite
        /// </summary>
        private async Task EvictOldestCacheEntriesAsync()
        {
            await Task.Run(() =>
            {
                var entriesToRemove = _engineCache.Count - _options.MaxCachedEngines + 1;
                var keysToRemove = _engineCache.Keys.Take(entriesToRemove).ToList();

                foreach (var key in keysToRemove)
                {
                    if (_engineCache.TryRemove(key, out var engineToDispose))
                    {
                        try
                        {
                            // RulesEngine 6.0.0 no implementa IDisposable
                            // engineToDispose?.Dispose();
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Error liberando engine durante eviction: {Key}", key);
                        }
                    }
                }

                _logger.LogDebug("Cache eviction completado. Entries removidas: {Count}", keysToRemove.Count);
            });
        }

        /// <summary>
        /// Libera recursos y limpia el cache
        /// </summary>
        public void Dispose()
        {
            if (_disposed)
                return;

            _logger.LogInformation("Liberando recursos del RuleEngineService");

            ClearCache(includeMetrics: true);

            _disposed = true;
            
            _logger.LogInformation("RuleEngineService liberado exitosamente");
        }
    }

    /// <summary>
    /// Clase para métricas de ejecución de reglas
    /// </summary>
    public sealed class RuleExecutionMetrics
    {
        private readonly object _lock = new();

        public string RuleName { get; }
        public string WorkflowName { get; }
        public int TotalExecutions { get; private set; }
        public int SuccessfulExecutions { get; private set; }
        public int FailedExecutions { get; private set; }
        public TimeSpan TotalExecutionTime { get; private set; }
        public TimeSpan AverageExecutionTime => TotalExecutions > 0 
            ? TimeSpan.FromTicks(TotalExecutionTime.Ticks / TotalExecutions) 
            : TimeSpan.Zero;
        public double SuccessRate => TotalExecutions > 0 
            ? (double)SuccessfulExecutions / TotalExecutions * 100 
            : 0;
        public DateTime LastExecutionUtc { get; private set; } = DateTime.UtcNow;

        public RuleExecutionMetrics(string ruleName, string workflowName)
        {
            RuleName = ruleName ?? throw new ArgumentNullException(nameof(ruleName));
            WorkflowName = workflowName ?? throw new ArgumentNullException(nameof(workflowName));
        }

        public void RecordExecution(TimeSpan executionTime, bool success)
        {
            lock (_lock)
            {
                TotalExecutions++;
                TotalExecutionTime = TotalExecutionTime.Add(executionTime);
                LastExecutionUtc = DateTime.UtcNow;

                if (success)
                    SuccessfulExecutions++;
                else
                    FailedExecutions++;
            }
        }

        public RuleExecutionMetrics Clone()
        {
            lock (_lock)
            {
                return new RuleExecutionMetrics(RuleName, WorkflowName)
                {
                    TotalExecutions = TotalExecutions,
                    SuccessfulExecutions = SuccessfulExecutions,
                    FailedExecutions = FailedExecutions,
                    TotalExecutionTime = TotalExecutionTime,
                    LastExecutionUtc = LastExecutionUtc
                };
            }
        }
    }

    /// <summary>
    /// Opciones de configuración para el RuleEngineService
    /// </summary>
    public sealed class RuleEngineServiceOptions
    {
        /// <summary>
        /// Tiempo de cache para workflows compilados en minutos (por defecto: 30)
        /// </summary>
        public int CacheExpirationMinutes { get; set; } = 30;

        /// <summary>
        /// Número máximo de engines compilados en cache (por defecto: 100)
        /// </summary>
        public int MaxCachedEngines { get; set; } = 100;

        /// <summary>
        /// Timeout para evaluación de reglas en milisegundos (por defecto: 5000)
        /// </summary>
        public int EvaluationTimeoutMs { get; set; } = 5000;

        /// <summary>
        /// Tipos personalizados para el engine de reglas
        /// </summary>
        public IList<Type> CustomTypes { get; set; } = new List<Type>();

        /// <summary>
        /// Indica si se deben ignorar excepciones durante la evaluación (por defecto: false)
        /// </summary>
        public bool IgnoreExceptions { get; set; } = false;

        /// <summary>
        /// Habilita modo debug para expresiones (por defecto: false)
        /// </summary>
        public bool EnableDebugMode { get; set; } = false;

        /// <summary>
        /// Parámetros adicionales para incluir en todas las evaluaciones
        /// </summary>
        public IDictionary<string, object> AdditionalParameters { get; set; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// Excepción específica para errores de evaluación de reglas
    /// </summary>
    public sealed class RuleEvaluationException : Exception
    {
        public RuleEvaluationException(string message) : base(message)
        {
        }

        public RuleEvaluationException(string message, Exception innerException) : base(message, innerException)
        {
        }
    }
}