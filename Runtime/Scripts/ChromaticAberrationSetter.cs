using UnityEngine;
using UnityEngine.UI;

namespace CallumNicholson.RaytraceGlassURP
{
    class ChromaticAberrationSetter : MonoBehaviour
    {
        [SerializeField] private Toggle enableToggle;

        public void Start()
        {
            enableToggle.onValueChanged.AddListener(_ => ToggleStatus());
            ToggleStatus();
        }

        private void ToggleStatus()
        {
            bool isOn = enableToggle.isOn;
            Shader.SetGlobalInt("_EnableChromaticAberration", isOn ? 1 : 0);
        }
    }
}
