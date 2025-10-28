using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Microsoft.Extensions.Logging;
using PromoEngine.Application;
using PromoEngine.Domain;
using RulesEngine.Models;

namespace PromoEngine.Infrastructure.Authoring
{
    /// <summary>
    /// Servicio de infraestructura que compila definiciones de promociones en flujos de trabajo ejecutables.
    /// Transforma las reglas de negocio definidas por los usuarios en expresiones lambda que pueden ser 
    /// evaluadas por el motor de reglas en tiempo de ejecución.
    /// 
    /// Implementa el patrón Compiler/Translator para convertir entre representaciones de alto nivel
    /// (DTOs de aplicación) y representaciones de bajo nivel (WorkflowRules ejecutables).
    /// </summary>
    public sealed class RulesEngineCompiler : IPromotionCompiler
    {
        private readonly ILogger<RulesEngineCompiler> _logger;

        // Constantes para operadores binarios soportados
        private static readonly IReadOnlyDictionary<string, string> BinaryOperatorMappings = 
            new Dictionary<string, string>
            {
                ["gt"] = ">",
                ["gte"] = ">=",
                ["lt"] = "<",
                ["lte"] = "<=",
                ["eq"] = "==",
                ["neq"] = "!="
            };

        // Constantes para generación de nombres
        private const string WorkflowNameTemplate = "promo:{0}:country:{1}";
        private const string RuleNameTemplate = "tier:{0}:group:{1}";
        private const string SuccessEventTemplate = "{0}:{1}";
        private const string ContextVariablePrefix = "ctx.";

        /// <summary>
        /// Inicializa una nueva instancia del compilador de reglas
        /// </summary>
        /// <param name="logger">Logger para trazabilidad y diagnósticos</param>
        /// <exception cref="ArgumentNullException">Cuando el logger es nulo</exception>
        public RulesEngineCompiler(ILogger<RulesEngineCompiler> logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>
        /// Construye un flujo de trabajo ejecutable a partir de una solicitud de promoción.
        /// Compila todas las reglas de negocio en expresiones lambda evaluables por el motor de reglas.
        /// </summary>
        /// <param name="request">Solicitud que contiene la definición de la promoción</param>
        /// <param name="attributeCatalog">Catálogo de atributos disponibles para construir expresiones</param>
        /// <param name="operatorCatalog">Catálogo de operadores disponibles para las expresiones</param>
        /// <param name="supportedOperatorTypes">Combinaciones válidas de operador-tipo de datos</param>
        /// <returns>Tupla con el flujo de trabajo compilado y lista de advertencias detectadas</returns>
        /// <exception cref="ArgumentNullException">Cuando algún parámetro requerido es nulo</exception>
        /// <exception cref="InvalidOperationException">Cuando ocurre un error durante la compilación</exception>
        public (WorkflowRules Workflow, List<string> Warnings) BuildWorkflow(
            UpsertPromotionDraftRequest request,
            IReadOnlyDictionary<Guid, AttributeCatalog> attributeCatalog,
            IReadOnlyDictionary<Guid, OperatorCatalog> operatorCatalog,
            IReadOnlySet<(Guid operatorId, DataType type)> supportedOperatorTypes)
        {
            // Validación de parámetros de entrada
            ValidateBuildParameters(request, attributeCatalog, operatorCatalog, supportedOperatorTypes);

            _logger.LogInformation(
                "Iniciando compilación de workflow. PromotionId: {PromotionId}, CountryIso: {CountryIso}, TierCount: {TierCount}",
                request.PromotionId, request.CountryIso, request.Tiers?.Count ?? 0);

            var compilationContext = new CompilationContext(
                attributeCatalog,
                operatorCatalog,
                supportedOperatorTypes,
                new List<string>());

            try
            {
                var workflow = CreateWorkflowStructure(request);
                CompilePromotionTiers(request, workflow, compilationContext);

                _logger.LogInformation(
                    "Compilación completada exitosamente. Rules: {RuleCount}, Warnings: {WarningCount}",
                    workflow.Rules.Count, compilationContext.Warnings.Count);

                if (compilationContext.Warnings.Any())
                {
                    _logger.LogWarning(
                        "Se detectaron advertencias durante la compilación: {Warnings}",
                        string.Join("; ", compilationContext.Warnings));
                }

                return (workflow, compilationContext.Warnings);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Error durante la compilación del workflow. PromotionId: {PromotionId}",
                    request.PromotionId);

                throw new InvalidOperationException(
                    $"Error al compilar el workflow para la promoción {request.PromotionId}", ex);
            }
        }

        /// <summary>
        /// Valida los parámetros de entrada del método BuildWorkflow
        /// </summary>
        /// <param name="request">Solicitud de promoción</param>
        /// <param name="attributeCatalog">Catálogo de atributos</param>
        /// <param name="operatorCatalog">Catálogo de operadores</param>
        /// <param name="supportedOperatorTypes">Tipos de operadores soportados</param>
        /// <exception cref="ArgumentNullException">Cuando algún parámetro es nulo</exception>
        /// <exception cref="ArgumentException">Cuando algún parámetro es inválido</exception>
        private static void ValidateBuildParameters(
            UpsertPromotionDraftRequest request,
            IReadOnlyDictionary<Guid, AttributeCatalog> attributeCatalog,
            IReadOnlyDictionary<Guid, OperatorCatalog> operatorCatalog,
            IReadOnlySet<(Guid operatorId, DataType type)> supportedOperatorTypes)
        {
            if (request == null)
                throw new ArgumentNullException(nameof(request));

            if (attributeCatalog == null)
                throw new ArgumentNullException(nameof(attributeCatalog));

            if (operatorCatalog == null)
                throw new ArgumentNullException(nameof(operatorCatalog));

            if (supportedOperatorTypes == null)
                throw new ArgumentNullException(nameof(supportedOperatorTypes));

            if (string.IsNullOrWhiteSpace(request.CountryIso))
                throw new ArgumentException("CountryIso no puede estar vacío", nameof(request));

            if (request.Tiers == null)
                throw new ArgumentException("La lista de tiers no puede ser nula", nameof(request));
        }

        /// <summary>
        /// Crea la estructura básica del workflow con metadatos
        /// </summary>
        /// <param name="request">Solicitud de promoción</param>
        /// <returns>Workflow inicializado con metadatos básicos</returns>
        private static WorkflowRules CreateWorkflowStructure(UpsertPromotionDraftRequest request)
        {
            var workflowName = string.Format(CultureInfo.InvariantCulture, 
                WorkflowNameTemplate, 
                request.PromotionId ?? Guid.Empty, 
                request.CountryIso);

            return new WorkflowRules
            {
                WorkflowName = workflowName,
                Rules = new List<Rule>()
            };
        }

        /// <summary>
        /// Compila todos los tiers de promoción en reglas ejecutables
        /// </summary>
        /// <param name="request">Solicitud de promoción</param>
        /// <param name="workflow">Workflow donde añadir las reglas</param>
        /// <param name="context">Contexto de compilación</param>
        private void CompilePromotionTiers(
            UpsertPromotionDraftRequest request, 
            WorkflowRules workflow, 
            CompilationContext context)
        {
            var orderedTiers = request.Tiers
                .OrderBy(tier => tier.TierLevel)
                .ThenBy(tier => tier.Order);

            foreach (var tier in orderedTiers)
            {
                CompileTierGroups(tier, workflow, context);
            }
        }

        /// <summary>
        /// Compila los grupos de expresiones de un tier específico
        /// </summary>
        /// <param name="tier">Tier a compilar</param>
        /// <param name="workflow">Workflow donde añadir las reglas</param>
        /// <param name="context">Contexto de compilación</param>
        private void CompileTierGroups(
            TierDto tier, 
            WorkflowRules workflow, 
            CompilationContext context)
        {
            if (tier.Groups == null || tier.Groups.Count == 0)
            {
                _logger.LogWarning(
                    "Tier {TierLevel} no tiene grupos de expresiones definidos",
                    tier.TierLevel);
                return;
            }

            var orderedGroups = tier.Groups.OrderBy(group => group.Order);
            var groupIndex = 0;

            foreach (var group in orderedGroups)
            {
                try
                {
                    CompileExpressionGroup(tier, group, groupIndex, workflow, context);
                    groupIndex++;
                }
                catch (Exception ex)
                {
                    var warning = $"Error compilando grupo {groupIndex} del tier {tier.TierLevel}: {ex.Message}";
                    context.Warnings.Add(warning);
                    
                    _logger.LogWarning(ex,
                        "Error compilando grupo de expresiones. Tier: {TierLevel}, Group: {GroupIndex}",
                        tier.TierLevel, groupIndex);
                }
            }
        }

        /// <summary>
        /// Compila un grupo de expresiones individual en una regla ejecutable
        /// </summary>
        /// <param name="tier">Tier padre</param>
        /// <param name="group">Grupo de expresiones a compilar</param>
        /// <param name="groupIndex">Índice del grupo dentro del tier</param>
        /// <param name="workflow">Workflow donde añadir la regla</param>
        /// <param name="context">Contexto de compilación</param>
        private void CompileExpressionGroup(
            TierDto tier,
            GroupDto group,
            int groupIndex,
            WorkflowRules workflow,
            CompilationContext context)
        {
            if (group.ExpressionRoot == null)
            {
                context.Warnings.Add($"Grupo {groupIndex} del tier {tier.TierLevel} no tiene expresión raíz definida");
                return;
            }

            var compiledExpression = CompileLogicNode(group.ExpressionRoot, context);

            var rule = new Rule
            {
                RuleName = string.Format(CultureInfo.InvariantCulture, 
                    RuleNameTemplate, 
                    tier.TierLevel, 
                    groupIndex),
                SuccessEvent = string.Format(CultureInfo.InvariantCulture, 
                    SuccessEventTemplate, 
                    tier.TierLevel, 
                    groupIndex),
                RuleExpressionType = RuleExpressionType.LambdaExpression,
                Expression = compiledExpression
            };

            workflow.Rules.Add(rule);

            _logger.LogDebug(
                "Regla compilada exitosamente. Tier: {TierLevel}, Group: {GroupIndex}, Expression: {Expression}",
                tier.TierLevel, groupIndex, compiledExpression);
        }

        /// <summary>
        /// Compila recursivamente un nodo lógico en una expresión lambda
        /// </summary>
        /// <param name="node">Nodo lógico a compilar</param>
        /// <param name="context">Contexto de compilación</param>
        /// <returns>Expresión lambda compilada</returns>
        /// <exception cref="InvalidOperationException">Cuando el nodo es inválido</exception>
        /// <exception cref="NotSupportedException">Cuando se encuentra un tipo o operador no soportado</exception>
        private string CompileLogicNode(LogicNodeDto node, CompilationContext context)
        {
            if (node == null)
                throw new ArgumentNullException(nameof(node));

            switch (node.Kind)
            {
                case LogicKind.Clause:
                    return CompileClauseNode(node, context);

                case LogicKind.Group:
                    return CompileGroupNode(node, context);

                default:
                    throw new NotSupportedException($"Tipo de nodo lógico no soportado: {node.Kind}");
            }
        }

        /// <summary>
        /// Compila un nodo de cláusula (comparación individual)
        /// </summary>
        /// <param name="node">Nodo de cláusula</param>
        /// <param name="context">Contexto de compilación</param>
        /// <returns>Expresión de comparación compilada</returns>
        /// <exception cref="InvalidOperationException">Cuando la cláusula es incompleta</exception>
        private string CompileClauseNode(LogicNodeDto node, CompilationContext context)
        {
            ValidateClauseNode(node);

            var attribute = context.AttributeCatalog[node.AttributeId!.Value];
            var operatorDefinition = context.OperatorCatalog[node.OperatorId!.Value];

            // Verificar compatibilidad de operador con tipo de datos
            ValidateOperatorCompatibility(attribute, operatorDefinition, context);

            var leftOperand = GenerateContextVariable(attribute.CanonicalName);
            
            return GenerateComparisonExpression(
                leftOperand, 
                operatorDefinition, 
                attribute.DataType, 
                node.ValueRaw!);
        }

        /// <summary>
        /// Compila un nodo de grupo (combinación lógica de múltiples nodos)
        /// </summary>
        /// <param name="node">Nodo de grupo</param>
        /// <param name="context">Contexto de compilación</param>
        /// <returns>Expresión de grupo compilada</returns>
        private string CompileGroupNode(LogicNodeDto node, CompilationContext context)
        {
            // Si no hay hijos, retornar expresión verdadera por defecto
            if (node.Children == null || node.Children.Count == 0)
            {
                _logger.LogDebug("Nodo de grupo sin hijos, retornando expresión verdadera por defecto");
                return "true";
            }

            var booleanOperator = DetermineBooleanOperator(node.BoolOperator);
            var orderedChildren = node.Children.OrderBy(child => child.Order ?? 0);

            var compiledChildren = orderedChildren
                .Select(child => CompileLogicNode(child, context))
                .ToList();

            // Combinar expresiones con el operador booleano
            var combinedExpression = string.Join(booleanOperator, compiledChildren);
            
            // Envolver en paréntesis para preservar precedencia
            return $"({combinedExpression})";
        }

        /// <summary>
        /// Valida que un nodo de cláusula tenga todos los campos requeridos
        /// </summary>
        /// <param name="node">Nodo a validar</param>
        /// <exception cref="InvalidOperationException">Cuando la cláusula es incompleta</exception>
        private static void ValidateClauseNode(LogicNodeDto node)
        {
            if (node.AttributeId == null)
                throw new InvalidOperationException("Cláusula incompleta: falta AttributeId");

            if (node.OperatorId == null)
                throw new InvalidOperationException("Cláusula incompleta: falta OperatorId");

            if (node.ValueRaw == null)
                throw new InvalidOperationException("Cláusula incompleta: falta ValueRaw");
        }

        /// <summary>
        /// Valida que un operador sea compatible con un tipo de datos específico
        /// </summary>
        /// <param name="attribute">Atributo con tipo de datos</param>
        /// <param name="operatorDefinition">Definición del operador</param>
        /// <param name="context">Contexto de compilación</param>
        private void ValidateOperatorCompatibility(
            AttributeCatalog attribute, 
            OperatorCatalog operatorDefinition, 
            CompilationContext context)
        {
            var operatorTypeKey = (operatorDefinition.Id, attribute.DataType);
            
            if (!context.SupportedOperatorTypes.Contains(operatorTypeKey))
            {
                var warning = $"Operador '{operatorDefinition.Code}' no soportado para tipo de datos '{attribute.DataType}' en atributo '{attribute.CanonicalName}'";
                context.Warnings.Add(warning);
                
                _logger.LogWarning(
                    "Incompatibilidad de operador detectada. Operator: {OperatorCode}, DataType: {DataType}, Attribute: {AttributeName}",
                    operatorDefinition.Code, attribute.DataType, attribute.CanonicalName);
            }
        }

        /// <summary>
        /// Determina el operador booleano basado en el valor numérico
        /// </summary>
        /// <param name="boolOperator">Valor numérico del operador booleano</param>
        /// <returns>Representación en string del operador</returns>
        private static string DetermineBooleanOperator(int boolOperator)
        {
            return boolOperator == (int)BoolOperator.And ? " && " : " || ";
        }

        /// <summary>
        /// Genera una variable de contexto segura para el motor de reglas
        /// </summary>
        /// <param name="canonicalName">Nombre canónico del atributo</param>
        /// <returns>Variable de contexto formateada</returns>
        private static string GenerateContextVariable(string canonicalName)
        {
            var safeName = SanitizeVariableName(canonicalName);
            return ContextVariablePrefix + safeName;
        }

        /// <summary>
        /// Genera la expresión de comparación basada en el tipo de datos y operador
        /// </summary>
        /// <param name="leftOperand">Operando izquierdo (variable de contexto)</param>
        /// <param name="operatorDefinition">Definición del operador</param>
        /// <param name="dataType">Tipo de datos del atributo</param>
        /// <param name="rawValue">Valor crudo para comparación</param>
        /// <returns>Expresión de comparación compilada</returns>
        /// <exception cref="NotSupportedException">Cuando el tipo de datos o operador no está soportado</exception>
        private static string GenerateComparisonExpression(
            string leftOperand, 
            OperatorCatalog operatorDefinition, 
            DataType dataType, 
            string rawValue)
        {
            return dataType switch
            {
                DataType.Number => GenerateNumericComparison(leftOperand, operatorDefinition.Code, rawValue),
                DataType.Bool => GenerateBooleanComparison(leftOperand, operatorDefinition.Code, rawValue),
                DataType.String => GenerateStringComparison(leftOperand, operatorDefinition.Code, rawValue),
                DataType.Date => GenerateDateComparison(leftOperand, operatorDefinition.Code, rawValue),
                DataType.StringArray => GenerateStringArrayComparison(leftOperand, operatorDefinition.Code, rawValue),
                _ => throw new NotSupportedException($"Tipo de datos no soportado: {dataType}")
            };
        }

        /// <summary>
        /// Genera comparación para valores numéricos
        /// </summary>
        private static string GenerateNumericComparison(string leftOperand, string operatorCode, string rawValue)
        {
            var binaryOperator = MapToBinaryOperator(operatorCode);
            var numericValue = ConvertToNumber(rawValue);
            return $"{leftOperand} {binaryOperator} {numericValue}";
        }

        /// <summary>
        /// Genera comparación para valores booleanos
        /// </summary>
        private static string GenerateBooleanComparison(string leftOperand, string operatorCode, string rawValue)
        {
            var binaryOperator = MapToBinaryOperator(operatorCode);
            var booleanValue = ConvertToBoolean(rawValue);
            return $"{leftOperand} {binaryOperator} {booleanValue}";
        }

        /// <summary>
        /// Genera comparación para valores de texto
        /// </summary>
        /// <exception cref="NotSupportedException">Cuando el operador no está soportado para strings</exception>
        private static string GenerateStringComparison(string leftOperand, string operatorCode, string rawValue)
        {
            return operatorCode switch
            {
                "eq" => $"{leftOperand} == {ConvertToString(rawValue)}",
                "contains" => $"{leftOperand}.Contains({ConvertToString(rawValue)})",
                _ => throw new NotSupportedException($"Operador '{operatorCode}' no soportado para tipo String")
            };
        }

        /// <summary>
        /// Genera comparación para valores de fecha
        /// </summary>
        private static string GenerateDateComparison(string leftOperand, string operatorCode, string rawValue)
        {
            var binaryOperator = MapToBinaryOperator(operatorCode);
            var dateValue = $"DateTimeOffset.Parse({ConvertToString(rawValue)})";
            return $"{leftOperand} {binaryOperator} {dateValue}";
        }

        /// <summary>
        /// Genera comparación para arrays de strings
        /// </summary>
        /// <exception cref="NotSupportedException">Cuando el operador no está soportado para arrays</exception>
        private static string GenerateStringArrayComparison(string leftOperand, string operatorCode, string rawValue)
        {
            return operatorCode switch
            {
                "in" => $"{leftOperand}.Contains({ConvertToString(rawValue)})",
                _ => throw new NotSupportedException($"Operador '{operatorCode}' no soportado para tipo StringArray")
            };
        }

        /// <summary>
        /// Mapea códigos de operadores a operadores binarios de C#
        /// </summary>
        /// <param name="operatorCode">Código del operador</param>
        /// <returns>Operador binario de C#</returns>
        /// <exception cref="NotSupportedException">Cuando el operador no está mapeado</exception>
        private static string MapToBinaryOperator(string operatorCode)
        {
            if (BinaryOperatorMappings.TryGetValue(operatorCode, out var mappedOperator))
            {
                return mappedOperator;
            }

            throw new NotSupportedException($"Operador no soportado: {operatorCode}");
        }

        /// <summary>
        /// Convierte un valor crudo a representación numérica válida para C#
        /// </summary>
        /// <param name="rawValue">Valor crudo</param>
        /// <returns>Representación numérica</returns>
        /// <exception cref="InvalidOperationException">Cuando el valor no es un número válido</exception>
        private static string ConvertToNumber(string rawValue)
        {
            if (double.TryParse(rawValue, NumberStyles.Any, CultureInfo.InvariantCulture, out var numericValue))
            {
                return numericValue.ToString(CultureInfo.InvariantCulture);
            }

            throw new InvalidOperationException($"Valor numérico inválido: {rawValue}");
        }

        /// <summary>
        /// Convierte un valor crudo a representación booleana válida para C#
        /// </summary>
        /// <param name="rawValue">Valor crudo</param>
        /// <returns>Representación booleana ("true" o "false")</returns>
        private static string ConvertToBoolean(string rawValue)
        {
            return rawValue.Equals("true", StringComparison.OrdinalIgnoreCase) ? "true" : "false";
        }

        /// <summary>
        /// Convierte un valor crudo a literal de string válido para C#, escapando caracteres especiales
        /// </summary>
        /// <param name="rawValue">Valor crudo</param>
        /// <returns>Literal de string escapado</returns>
        private static string ConvertToString(string rawValue)
        {
            var escapedValue = rawValue
                .Replace("\\", "\\\\")  // Escapar backslashes
                .Replace("\"", "\\\""); // Escapar comillas

            return $"\"{escapedValue}\"";
        }

        /// <summary>
        /// Sanitiza un nombre de variable para hacerlo válido en C#
        /// </summary>
        /// <param name="variableName">Nombre original de la variable</param>
        /// <returns>Nombre sanitizado válido para C#</returns>
        private static string SanitizeVariableName(string variableName)
        {
            return variableName
                .Replace(" ", "_")   // Reemplazar espacios con guiones bajos
                .Replace("-", "_");  // Reemplazar guiones con guiones bajos
        }
    }

    /// <summary>
    /// Contexto interno que mantiene el estado durante la compilación
    /// </summary>
    internal sealed class CompilationContext
    {
        public IReadOnlyDictionary<Guid, AttributeCatalog> AttributeCatalog { get; }
        public IReadOnlyDictionary<Guid, OperatorCatalog> OperatorCatalog { get; }
        public IReadOnlySet<(Guid operatorId, DataType type)> SupportedOperatorTypes { get; }
        public List<string> Warnings { get; }

        public CompilationContext(
            IReadOnlyDictionary<Guid, AttributeCatalog> attributeCatalog,
            IReadOnlyDictionary<Guid, OperatorCatalog> operatorCatalog,
            IReadOnlySet<(Guid operatorId, DataType type)> supportedOperatorTypes,
            List<string> warnings)
        {
            AttributeCatalog = attributeCatalog ?? throw new ArgumentNullException(nameof(attributeCatalog));
            OperatorCatalog = operatorCatalog ?? throw new ArgumentNullException(nameof(operatorCatalog));
            SupportedOperatorTypes = supportedOperatorTypes ?? throw new ArgumentNullException(nameof(supportedOperatorTypes));
            Warnings = warnings ?? throw new ArgumentNullException(nameof(warnings));
        }
    }
}