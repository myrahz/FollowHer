using FollowHer.Settings;
using System;

namespace FollowHer.Features.Targeting
{
    public class TargetingSettingsMonitor : IDisposable
    {
        public event Action OnSettingsChanged;

        private readonly TargetingSettings _settings;
        private bool _disposed;

        public TargetingSettingsMonitor(TargetingSettings settings)
        {
            _settings = settings;
            SubscribeToChanges();
        }

        private void HandleSettingChange(object sender, bool e) => OnSettingsChanged?.Invoke();
        private void HandleSettingChange(object sender, int e) => OnSettingsChanged?.Invoke();
        private void HandleSettingChange(object sender, float e) => OnSettingsChanged?.Invoke();

        private void SubscribeToChanges()
        {
            _settings.TargetSwitchThreshold.OnValueChanged += HandleSettingChange;
            _settings.MaxTargetRange.OnValueChanged += HandleSettingChange;
            _settings.ScanRadius.OnValueChanged += HandleSettingChange;

            var density = _settings.Density;
            density.EnableClustering.OnValueChanged += HandleSettingChange;
            density.ClusterRadius.OnValueChanged += HandleSettingChange;
            density.MinClusterSize.OnValueChanged += HandleSettingChange;
            density.BaseClusterBonus.OnValueChanged += HandleSettingChange;
            density.MaxClusterBonus.OnValueChanged += HandleSettingChange;
            density.EnableCoreBonus.OnValueChanged += HandleSettingChange;
            density.CoreRadiusPercent.OnValueChanged += HandleSettingChange;
            density.CoreBonusMultiplier.OnValueChanged += HandleSettingChange;
            density.EnableIsolationPenalty.OnValueChanged += HandleSettingChange;
            density.IsolationPenaltyMultiplier.OnValueChanged += HandleSettingChange;

            var los = _settings.LineOfSight;
            los.RequireLineOfSight.OnValueChanged += HandleSettingChange;

            var priorities = _settings.Priorities;
            priorities.DistanceWeight.OnValueChanged += HandleSettingChange;
            priorities.Health.HealthWeight.OnValueChanged += HandleSettingChange;
            priorities.Health.PreferHigherHealth.OnValueChanged += HandleSettingChange;
            priorities.Rarity.ConsiderRarity.OnValueChanged += HandleSettingChange;
        }

        private void UnsubscribeFromChanges()
        {
            _settings.TargetSwitchThreshold.OnValueChanged -= HandleSettingChange;
            _settings.MaxTargetRange.OnValueChanged -= HandleSettingChange;
            _settings.ScanRadius.OnValueChanged -= HandleSettingChange;
            
            var density = _settings.Density;
            density.EnableClustering.OnValueChanged -= HandleSettingChange;
            density.ClusterRadius.OnValueChanged -= HandleSettingChange;
            density.MinClusterSize.OnValueChanged -= HandleSettingChange;
            density.BaseClusterBonus.OnValueChanged -= HandleSettingChange;
            density.MaxClusterBonus.OnValueChanged -= HandleSettingChange;
            density.EnableCoreBonus.OnValueChanged -= HandleSettingChange;
            density.CoreRadiusPercent.OnValueChanged -= HandleSettingChange;
            density.CoreBonusMultiplier.OnValueChanged -= HandleSettingChange;
            density.EnableIsolationPenalty.OnValueChanged -= HandleSettingChange;
            density.IsolationPenaltyMultiplier.OnValueChanged -= HandleSettingChange;

            var los = _settings.LineOfSight;
            los.RequireLineOfSight.OnValueChanged -= HandleSettingChange;
            
            var priorities = _settings.Priorities;
            priorities.DistanceWeight.OnValueChanged -= HandleSettingChange;
            priorities.Health.HealthWeight.OnValueChanged -= HandleSettingChange;
            priorities.Health.PreferHigherHealth.OnValueChanged -= HandleSettingChange;
            priorities.Rarity.ConsiderRarity.OnValueChanged -= HandleSettingChange;
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                UnsubscribeFromChanges();
                _disposed = true;
            }
        }
    }
} 