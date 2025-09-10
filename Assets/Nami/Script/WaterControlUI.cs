using UnityEngine;
using UnityEngine.UI;
using WaterSystem;
using WaterSystem.Data;

namespace Nami
{
    /// <summary>
    /// Simple runtime UI to control water appearance parameters.
    /// Attach this to any GameObject with Canvas/UI elements, or use the debug keys.
    /// </summary>
    public class WaterControlUI : MonoBehaviour
    {
        [Header("UI References (optional - leave null to use debug keys only)")]
        public Slider absorptionGainSlider;
        public Slider scatteringGainSlider;
        public Slider foamGainSlider;
        public Slider gammaSlider;
        public Dropdown reflectionTypeDropdown;
        public Text statusText;

        [Header("Debug Keys")]
        public KeyCode toggleUIKey = KeyCode.F1;
        public KeyCode resetToDefaultsKey = KeyCode.F2;
        public KeyCode planarReflectionKey = KeyCode.F3;

        [Header("Current Values")]
        [SerializeField] private float absorptionGain = 1f;
        [SerializeField] private float scatteringGain = 1f;
        [SerializeField] private float foamGain = 1f;
        [SerializeField] private float gamma = 1f;
        [SerializeField] private ReflectionType reflectionType = ReflectionType.ReflectionProbe;

        private bool uiVisible = true;

        private void Start()
        {
            InitializeUI();
            UpdateWaterParameters();
        }

        private void Update()
        {
            HandleDebugKeys();
        }

        private void HandleDebugKeys()
        {
            if (Input.GetKeyDown(toggleUIKey))
            {
                ToggleUI();
            }

            if (Input.GetKeyDown(resetToDefaultsKey))
            {
                ResetToDefaults();
            }

            if (Input.GetKeyDown(planarReflectionKey))
            {
                TogglePlanarReflection();
            }

            // Arrow keys for quick adjustments
            if (Input.GetKey(KeyCode.LeftShift))
            {
                if (Input.GetKeyDown(KeyCode.UpArrow))
                    AdjustParameter(0, 0.1f);
                if (Input.GetKeyDown(KeyCode.DownArrow))
                    AdjustParameter(0, -0.1f);
                if (Input.GetKeyDown(KeyCode.LeftArrow))
                    AdjustParameter(1, -0.1f);
                if (Input.GetKeyDown(KeyCode.RightArrow))
                    AdjustParameter(1, 0.1f);
            }
        }

        private void AdjustParameter(int param, float delta)
        {
            switch (param)
            {
                case 0: // absorption
                    absorptionGain = Mathf.Clamp(absorptionGain + delta, 0.25f, 4f);
                    break;
                case 1: // scattering
                    scatteringGain = Mathf.Clamp(scatteringGain + delta, 0.25f, 4f);
                    break;
            }
            UpdateWaterParameters();
            UpdateUI();
        }

        private void InitializeUI()
        {
            if (absorptionGainSlider != null)
            {
                absorptionGainSlider.minValue = 0.25f;
                absorptionGainSlider.maxValue = 4f;
                absorptionGainSlider.value = absorptionGain;
                absorptionGainSlider.onValueChanged.AddListener(OnAbsorptionGainChanged);
            }

            if (scatteringGainSlider != null)
            {
                scatteringGainSlider.minValue = 0.25f;
                scatteringGainSlider.maxValue = 4f;
                scatteringGainSlider.value = scatteringGain;
                scatteringGainSlider.onValueChanged.AddListener(OnScatteringGainChanged);
            }

            if (foamGainSlider != null)
            {
                foamGainSlider.minValue = 0f;
                foamGainSlider.maxValue = 4f;
                foamGainSlider.value = foamGain;
                foamGainSlider.onValueChanged.AddListener(OnFoamGainChanged);
            }

            if (gammaSlider != null)
            {
                gammaSlider.minValue = 0.5f;
                gammaSlider.maxValue = 2f;
                gammaSlider.value = gamma;
                gammaSlider.onValueChanged.AddListener(OnGammaChanged);
            }

            if (reflectionTypeDropdown != null)
            {
                reflectionTypeDropdown.ClearOptions();
                reflectionTypeDropdown.AddOptions(new System.Collections.Generic.List<string>
                {
                    "Cubemap", "Reflection Probe", "Planar Reflection"
                });
                reflectionTypeDropdown.value = (int)reflectionType;
                reflectionTypeDropdown.onValueChanged.AddListener(OnReflectionTypeChanged);
            }

            UpdateUI();
        }

        private void UpdateUI()
        {
            if (statusText != null)
            {
                statusText.text = $"Water Control\n" +
                                $"Absorption: {absorptionGain:F2}\n" +
                                $"Scattering: {scatteringGain:F2}\n" +
                                $"Foam: {foamGain:F2}\n" +
                                $"Gamma: {gamma:F2}\n" +
                                $"Reflection: {reflectionType}\n\n" +
                                $"Keys: F1=Toggle UI, F2=Reset, F3=Planar\n" +
                                $"Shift+Arrows: Quick adjust";
            }
        }

        private void UpdateWaterParameters()
        {
            if (Water.Instance != null)
            {
                Water.Instance.SetWaterColorGains(absorptionGain, scatteringGain, foamGain, gamma);
                Water.Instance.SetReflectionType(reflectionType);
            }
        }

        // UI Event Handlers
        private void OnAbsorptionGainChanged(float value)
        {
            absorptionGain = value;
            UpdateWaterParameters();
        }

        private void OnScatteringGainChanged(float value)
        {
            scatteringGain = value;
            UpdateWaterParameters();
        }

        private void OnFoamGainChanged(float value)
        {
            foamGain = value;
            UpdateWaterParameters();
        }

        private void OnGammaChanged(float value)
        {
            gamma = value;
            UpdateWaterParameters();
        }

        private void OnReflectionTypeChanged(int value)
        {
            reflectionType = (ReflectionType)value;
            UpdateWaterParameters();
        }

        // Debug Functions
        private void ToggleUI()
        {
            uiVisible = !uiVisible;
            if (absorptionGainSlider != null) absorptionGainSlider.gameObject.SetActive(uiVisible);
            if (scatteringGainSlider != null) scatteringGainSlider.gameObject.SetActive(uiVisible);
            if (foamGainSlider != null) foamGainSlider.gameObject.SetActive(uiVisible);
            if (gammaSlider != null) gammaSlider.gameObject.SetActive(uiVisible);
            if (reflectionTypeDropdown != null) reflectionTypeDropdown.gameObject.SetActive(uiVisible);
            if (statusText != null) statusText.gameObject.SetActive(uiVisible);
        }

        private void ResetToDefaults()
        {
            absorptionGain = 1f;
            scatteringGain = 1f;
            foamGain = 1f;
            gamma = 1f;
            reflectionType = ReflectionType.ReflectionProbe;
            UpdateWaterParameters();
            UpdateUI();
        }

        private void TogglePlanarReflection()
        {
            reflectionType = reflectionType == ReflectionType.PlanarReflection 
                ? ReflectionType.ReflectionProbe 
                : ReflectionType.PlanarReflection;
            UpdateWaterParameters();
            UpdateUI();
        }

        // Public API for other scripts
        public void SetWaterBrightness(float brightness)
        {
            // Simple brightness control - affects both absorption and scattering
            absorptionGain = Mathf.Clamp(brightness, 0.25f, 4f);
            scatteringGain = Mathf.Clamp(brightness * 1.1f, 0.25f, 4f);
            UpdateWaterParameters();
            UpdateUI();
        }

        public void SetWaterContrast(float contrast)
        {
            // Simple contrast control - affects gamma
            gamma = Mathf.Clamp(contrast, 0.5f, 2f);
            UpdateWaterParameters();
            UpdateUI();
        }
    }
}
