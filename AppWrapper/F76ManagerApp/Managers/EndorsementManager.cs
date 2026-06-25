using System;
using System.IO;
using System.Text.Json;
using System.Windows.Forms;
using System.Diagnostics;

namespace F76ManagerApp.Managers
{
    public class EndorsementManager
    {
        private class EndorsementState
        {
            public int TotalSecondsRun { get; set; } = 0;
            public bool HasShown { get; set; } = false;
        }

        private const int ThresholdSeconds = 1200;
        private const string NexusUrl = "https://www.nexusmods.com/fallout76/mods/3674?tab=files";
        
        private readonly string _statsFile;
        private readonly Action<string> _logger;
        private readonly Action _onThresholdReached;
        private EndorsementState _state;
        private bool _sessionHasTriggered = false;

        public EndorsementManager(string statsFile, Action<string> logger, Action onThresholdReached)
        {
            _statsFile = statsFile;
            _logger = logger;
            _onThresholdReached = onThresholdReached;
            LoadState();
        }

        public void Tick(int seconds)
        {
            if (_state.HasShown || _sessionHasTriggered) return;

            _state.TotalSecondsRun += seconds;
            
            if (_state.TotalSecondsRun >= ThresholdSeconds)
            {
                TriggerEndorsement();
            }
            else if (_state.TotalSecondsRun % 60 == 0)
            {
                SaveState();
            }
        }

        public void ConfirmEndorsement()
        {
            _logger?.Invoke("[ENDORSE] User confirmed endorsement.");
            _state.HasShown = true;
            SaveState();
        }

        public void DismissEndorsement()
        {
            _logger?.Invoke("[ENDORSE] User dismissed endorsement. Marking as shown (One-time only).");
            _state.HasShown = true;
            SaveState();
        }

        private void LoadState()
        {
            try
            {
                if (File.Exists(_statsFile))
                {
                    string json = File.ReadAllText(_statsFile);
                    _state = JsonSerializer.Deserialize<EndorsementState>(json) ?? new EndorsementState();
                }
                else
                {
                    _state = new EndorsementState();
                }
            }
            catch (Exception ex)
            {
                _logger?.Invoke($"[ENDORSE] Failed to load endorsement stats: {ex.Message}");
                _state = new EndorsementState();
            }
        }

        private void SaveState()
        {
            try
            {
                string dir = Path.GetDirectoryName(_statsFile);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

                string json = JsonSerializer.Serialize(_state, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_statsFile, json);
            }
            catch (Exception ex)
            {
                _logger?.Invoke($"[ENDORSE] Failed to save endorsement stats: {ex.Message}");
            }
        }

        private void TriggerEndorsement()
        {
            _sessionHasTriggered = true;
            _logger?.Invoke("[ENDORSE] Threshold reached. Triggering frontend modal.");
            _onThresholdReached?.Invoke();
        }
    }
}
