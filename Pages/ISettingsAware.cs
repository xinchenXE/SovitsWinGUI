namespace SoVITS_GUI.Pages
{
    public interface ISettingsAware
    {
        void OnSettingsChanged();
        void OnPaneStateChanged(bool isPaneOpen);
    }
}
