using System;
using System.Collections.Generic;
using System.Linq;

namespace PromoEngine.Domain
{
    // ========================================
    // DOMAIN SERVICES & SPECIFICATIONS
    // ========================================

    /// <summary>
    /// Servicio de dominio para validar reglas de negocio complejas relacionadas con promociones
    /// </summary>
    public sealed class PromotionDomainService
    {
        /// <summary>
        /// Verifica si una promoción puede ser activada en base a sus versiones
        /// </summary>
        /// <param name="promotion">Promoción a validar</param>
        /// <param name="targetCountry">País objetivo</param>
        /// <param name="currentDate">Fecha actual</param>
        /// <returns>True si puede ser activada</returns>
        public bool CanActivatePromotion(Promotion promotion, string targetCountry, DateTimeOffset currentDate)
        {
            // Debe tener al menos una versión publicada para el país
            var publishedVersion = promotion.Versions
                .Where(v => !v.IsDraft && 
                           v.CountryIso.Equals(targetCountry, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(v => v.Version)
                .FirstOrDefault();

            if (publishedVersion == null)
                return false;

            // La versión debe estar dentro del período de validez
            return publishedVersion.ValidityPeriod.IsActiveAt(currentDate);
        }

        /// <summary>
        /// Calcula el siguiente número de versión para una promoción en un país específico
        /// </summary>
        /// <param name="promotion">Promoción</param>
        /// <param name="countryIso">Código ISO del país</param>
        /// <returns>Siguiente número de versión</returns>
        public int CalculateNextVersion(Promotion promotion, string countryIso)
        {
            var maxVersion = promotion.Versions
                .Where(v => v.CountryIso.Equals(countryIso, StringComparison.OrdinalIgnoreCase))
                .Select(v => v.Version)
                .DefaultIfEmpty(0)
                .Max();

            return maxVersion + 1;
        }

        /// <summary>
        /// Verifica si una promoción tiene conflictos con otra promoción existente
        /// </summary>
        /// <param name="newPromotion">Nueva promoción</param>
        /// <param name="existingPromotions">Promociones existentes</param>
        /// <returns>True si hay conflictos</returns>
        public bool HasConflicts(Promotion newPromotion, IEnumerable<Promotion> existingPromotions)
        {
            // Verificar nombres duplicados (simplificado)
            return existingPromotions.Any(p => 
                p.Id != newPromotion.Id && 
                p.Name.Equals(newPromotion.Name, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>
    /// Servicio de dominio para gestionar el cooldown de recompensas
    /// </summary>
    public sealed class RewardCooldownService
    {
        /// <summary>
        /// Calcula la fecha hasta la cual un contacto estará en cooldown
        /// </summary>
        /// <param name="lastGrantDate">Fecha del último otorgamiento</param>
        /// <param name="cooldownDays">Días de cooldown</param>
        /// <returns>Fecha límite del cooldown</returns>
        public DateTimeOffset CalculateCooldownUntil(DateTimeOffset lastGrantDate, int cooldownDays)
        {
            return lastGrantDate.AddDays(cooldownDays);
        }

        /// <summary>
        /// Verifica si un contacto puede recibir una nueva recompensa basándose en el historial
        /// </summary>
        /// <param name="contactId">ID del contacto</param>
        /// <param name="promotionId">ID de la promoción</param>
        /// <param name="globalCooldownDays">Días de cooldown global</param>
        /// <param name="tierCooldownDays">Días de cooldown del tier (opcional)</param>
        /// <param name="rewardHistory">Historial de recompensas del contacto</param>
        /// <param name="currentDate">Fecha actual</param>
        /// <returns>True si puede recibir la recompensa</returns>
        public bool CanReceiveReward(
            Guid contactId,
            Guid promotionId,
            int globalCooldownDays,
            int? tierCooldownDays,
            IEnumerable<ContactReward> rewardHistory,
            DateTimeOffset currentDate)
        {
            var effectiveCooldownDays = tierCooldownDays ?? globalCooldownDays;
            
            var lastGrantedReward = rewardHistory
                .Where(r => r.ContactId == contactId && 
                           r.PromotionId == promotionId && 
                           r.Status == RewardGrantStatus.Granted)
                .OrderByDescending(r => r.GrantedAt)
                .FirstOrDefault();

            if (lastGrantedReward == null)
                return true;

            var cooldownUntil = CalculateCooldownUntil(lastGrantedReward.GrantedAt, effectiveCooldownDays);
            return currentDate >= cooldownUntil;
        }

        /// <summary>
        /// Obtiene todos los contactos que salen del cooldown en una fecha específica
        /// </summary>
        /// <param name="targetDate">Fecha objetivo</param>
        /// <param name="rewardHistory">Historial de recompensas</param>
        /// <returns>Lista de ContactReward que salen del cooldown</returns>
        public IEnumerable<ContactReward> GetRewardsExitingCooldown(
            DateTimeOffset targetDate,
            IEnumerable<ContactReward> rewardHistory)
        {
            return rewardHistory.Where(r => 
                r.CooldownUntil.HasValue && 
                r.CooldownUntil.Value.Date == targetDate.Date);
        }
    }

    /// <summary>
    /// Servicio de dominio para validar expresiones de reglas
    /// </summary>
    public sealed class RuleExpressionValidationService
    {
        /// <summary>
        /// Valida que una expresión JSON tenga la estructura correcta
        /// </summary>
        /// <param name="expressionJson">Expresión en formato JSON</param>
        /// <returns>True si la expresión es válida</returns>
        public bool IsValidExpression(string expressionJson)
        {
            if (string.IsNullOrWhiteSpace(expressionJson))
                return false;

            try
            {
                // Validación básica de JSON válido
                System.Text.Json.JsonDocument.Parse(expressionJson);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Extrae los atributos referenciados en una expresión
        /// </summary>
        /// <param name="expressionJson">Expresión JSON</param>
        /// <returns>Lista de nombres de atributos referenciados</returns>
        public IEnumerable<string> ExtractReferencedAttributes(string expressionJson)
        {
            var attributes = new List<string>();

            if (string.IsNullOrWhiteSpace(expressionJson))
                return attributes;

            try
            {
                using var document = System.Text.Json.JsonDocument.Parse(expressionJson);
                ExtractAttributesRecursive(document.RootElement, attributes);
            }
            catch
            {
                // Si no se puede parsear, retornar lista vacía
            }

            return attributes.Distinct();
        }

        private void ExtractAttributesRecursive(System.Text.Json.JsonElement element, List<string> attributes)
        {
            if (element.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                foreach (var property in element.EnumerateObject())
                {
                    if (property.Name.Equals("attribute", StringComparison.OrdinalIgnoreCase) && 
                        property.Value.ValueKind == System.Text.Json.JsonValueKind.String)
                    {
                        attributes.Add(property.Value.GetString() ?? string.Empty);
                    }
                    else
                    {
                        ExtractAttributesRecursive(property.Value, attributes);
                    }
                }
            }
            else if (element.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                foreach (var item in element.EnumerateArray())
                {
                    ExtractAttributesRecursive(item, attributes);
                }
            }
        }
    }

    /// <summary>
    /// Especificación para determinar si una recompensa es elegible para un contacto
    /// </summary>
    public sealed class RewardEligibilitySpecification
    {
        private readonly RewardCooldownService _cooldownService;

        public RewardEligibilitySpecification(RewardCooldownService cooldownService)
        {
            _cooldownService = cooldownService ?? throw new ArgumentNullException(nameof(cooldownService));
        }

        /// <summary>
        /// Evalúa si una recompensa es elegible para un contacto
        /// </summary>
        /// <param name="contactId">ID del contacto</param>
        /// <param name="reward">Recompensa a evaluar</param>
        /// <param name="promotion">Promoción asociada</param>
        /// <param name="ruleTier">Tier de reglas</param>
        /// <param name="rewardHistory">Historial de recompensas</param>
        /// <param name="evaluationDate">Fecha de evaluación</param>
        /// <returns>True si es elegible</returns>
        public bool IsSatisfiedBy(
            Guid contactId,
            Reward reward,
            Promotion promotion,
            RuleTier ruleTier,
            IEnumerable<ContactReward> rewardHistory,
            DateTimeOffset evaluationDate)
        {
            // La recompensa debe estar activa
            if (!reward.IsActive)
                return false;

            // Verificar cooldown
            if (!_cooldownService.CanReceiveReward(
                contactId,
                promotion.Id,
                promotion.GlobalCooldownDays,
                ruleTier.CooldownDays,
                rewardHistory,
                evaluationDate))
            {
                return false;
            }

            return true;
        }
    }

    /// <summary>
    /// Especificación para determinar si una promoción está activa
    /// </summary>
    public sealed class ActivePromotionSpecification
    {
        /// <summary>
        /// Evalúa si una promoción está activa para un país y fecha específicos
        /// </summary>
        /// <param name="promotion">Promoción a evaluar</param>
        /// <param name="countryIso">Código ISO del país</param>
        /// <param name="evaluationDate">Fecha de evaluación</param>
        /// <returns>True si está activa</returns>
        public bool IsSatisfiedBy(Promotion promotion, string countryIso, DateTimeOffset evaluationDate)
        {
            var activeVersion = promotion.Versions
                .Where(v => !v.IsDraft && 
                           v.CountryIso.Equals(countryIso, StringComparison.OrdinalIgnoreCase))
                .Where(v => v.ValidityPeriod.IsActiveAt(evaluationDate))
                .OrderByDescending(v => v.Version)
                .FirstOrDefault();

            return activeVersion != null;
        }
    }

    /// <summary>
    /// Factory para crear instancias de Value Objects con validaciones específicas
    /// </summary>
    public static class DomainValueObjectFactory
    {
        /// <summary>
        /// Crea un valor monetario para recompensas validando reglas de negocio específicas
        /// </summary>
        /// <param name="amount">Cantidad</param>
        /// <param name="unit">Unidad</param>
        /// <param name="rewardType">Tipo de recompensa</param>
        /// <returns>Valor monetario válido</returns>
        /// <exception cref="InvalidMonetaryValueException">Cuando los valores no son válidos para el tipo de recompensa</exception>
        public static MonetaryValue CreateRewardValue(decimal amount, string unit, RewardType rewardType)
        {
            // Validaciones específicas por tipo de recompensa
            switch (rewardType)
            {
                case RewardType.Points:
                    if (unit != "points" && unit != "puntos")
                        throw new InvalidMonetaryValueException("Los puntos deben tener unidad 'points' o 'puntos'", amount, unit);
                    if (amount != Math.Floor(amount))
                        throw new InvalidMonetaryValueException("Los puntos deben ser números enteros", amount, unit);
                    break;

                case RewardType.Cashback:
                    if (string.IsNullOrEmpty(unit) || (!unit.Contains("EUR") && !unit.Contains("USD") && !unit.Contains("%")))
                        throw new InvalidMonetaryValueException("El cashback debe tener una unidad monetaria válida o porcentaje", amount, unit);
                    break;

                case RewardType.Coupon:
                    if (amount <= 0)
                        throw new InvalidMonetaryValueException("Los cupones deben tener un valor positivo", amount, unit);
                    break;
            }

            return MonetaryValue.Create(amount, unit);
        }

        /// <summary>
        /// Crea un período de validez con validaciones de negocio específicas
        /// </summary>
        /// <param name="validFromUtc">Fecha de inicio</param>
        /// <param name="validToUtc">Fecha de fin</param>
        /// <param name="allowPastDates">Permite fechas en el pasado</param>
        /// <returns>Período de validez</returns>
        /// <exception cref="InvalidValidityPeriodException">Cuando las fechas no cumplen las reglas de negocio</exception>
        public static ValidityPeriod CreateValidityPeriod(
            DateTimeOffset? validFromUtc, 
            DateTimeOffset? validToUtc, 
            bool allowPastDates = false)
        {
            if (!allowPastDates)
            {
                var now = DateTimeOffset.UtcNow;
                if (validFromUtc.HasValue && validFromUtc.Value < now)
                    throw new InvalidValidityPeriodException(validFromUtc, validToUtc);
                
                if (validToUtc.HasValue && validToUtc.Value < now)
                    throw new InvalidValidityPeriodException(validFromUtc, validToUtc);
            }

            // Validar que el período no sea demasiado largo (ej. máximo 5 años)
            if (validFromUtc.HasValue && validToUtc.HasValue)
            {
                var duration = validToUtc.Value - validFromUtc.Value;
                if (duration.TotalDays > 365 * 5) // 5 años
                    throw new InvalidValidityPeriodException(validFromUtc, validToUtc);
            }

            return ValidityPeriod.Create(validFromUtc, validToUtc);
        }
    }
}