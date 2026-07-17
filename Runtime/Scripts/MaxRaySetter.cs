using UnityEngine;
using TMPro;

namespace CallumNicholson.RaytraceGlassURP
{
    class MaxRaySetter : MonoBehaviour
    {
        [SerializeField] private TMP_InputField input;
        [SerializeField] private int defaultValue;

        public void Awake()
        {
            input.text = defaultValue.ToString();
            input.onValueChanged.AddListener(
                s =>
                {
                    if (!int.TryParse(s, out var maxRays)) return;
                    UpdateValue(maxRays);
                }
            );

            UpdateValue(defaultValue);
        }

        private void UpdateValue(int maxRays)
        {
            Shader.SetGlobalInt("_DynamicMaxRays", maxRays);
        }
    }
}
