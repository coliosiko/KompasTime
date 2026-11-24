using System.Runtime.InteropServices;
using Kompas6Constants;
using KompasAPI7;
using KompasLibrary;

[assembly: ComVisible(false)]

namespace KompasTime.Plugin;

[ComVisible(true)]
[Guid("73614771-A0CC-40A2-958B-1E080AB3DCCD")]
[ProgId("KompasTime.Menu")]
[ClassInterface(ClassInterfaceType.AutoDual)]
public sealed class KompasAddIn : IKompasLibrary
{
    private OverlayTracker? tracker;

    public bool get_IsFunctionEnable(ksKompasLibraryFunctionEnum id) =>
        id is ksKompasLibraryFunctionEnum.ksLFLibraryName
            or ksKompasLibraryFunctionEnum.ksLFDisplayLibraryName
            or ksKompasLibraryFunctionEnum.ksLFIsOnApplication7
            or ksKompasLibraryFunctionEnum.ksLFInitLibrary
            or ksKompasLibraryFunctionEnum.ksLFBeginUnloadLibrary
            or ksKompasLibraryFunctionEnum.ksLFFillLibraryMenu
            or ksKompasLibraryFunctionEnum.ksLFRunLibraryCommand
            or ksKompasLibraryFunctionEnum.ksLFGetLibraryCommandState;

    public bool InitLibrary([In, MarshalAs(UnmanagedType.IDispatch)] object applicationInterface)
    {
        if (applicationInterface is not IApplication application)
            return false;
        tracker ??= new OverlayTracker(application, SettingsForm.AutoConnect);
        return true;
    }

    public bool BeginUnloadLibrary()
    {
        tracker?.Dispose();
        tracker = null;
        return true;
    }

    public bool FillLibraryMenu(IKompasLibraryMenu menu)
    {
        menu.AddMenuCommand(1, "Вкл/выкл учёт трудозатрат");
        menu.AddMenuCommand(2, "Показать/скрыть значок состояния");
        menu.AddMenuCommand(3, "История");
        menu.AddMenuCommand(4, "Сводные данные");
        menu.AddMenuCommand(7, "Отчёт по файлам");
        menu.AddMenuCommand(5, "Настройки…");
        menu.AddMenuCommand(6, "Справка");
        return true;
    }

    public int RunLibraryCommand(int command, int demoMode)
    {
        if (tracker == null)
            return 0;
        switch (command)
        {
            case 1: tracker.ToggleTracking(); break;
            case 2: tracker.ToggleIcon(); break;
            case 3: tracker.ShowHistory(); break;
            case 4: tracker.ShowReport(); break;
            case 5: tracker.ShowSettings(); break;
            case 6: tracker.ShowHelp(); break;
            case 7: tracker.ShowFileReport(); break;
            default: return 0;
        }
        return 1;
    }

    public bool GetLibraryCommandState(int command, out bool enable, out int check)
    {
        enable = command switch
        {
            3 => tracker?.HasDocument == true,
            4 => tracker?.AssemblyOpen == true,
            _ => true
        };
        // KOMPAS caches command captions and queries the check state on each opening.
        check = command switch
        {
            1 => tracker?.Tracking == true ? 1 : 0,
            2 => SettingsForm.IconVisible ? 1 : 0,
            _ => 0
        };
        return true;
    }

    public string GetDisableReason(int command) => "";
    public bool FillContextPanel(object contextPanel) => false;
    public bool ContextPanelStyleComboChanged(string styleComboId, int styleType, int newValue) => false;
    public object? GetKompasConverter() => null;
    public bool CreateMacroFromSample(int macroReference) => false;
    public int Version => 1;
    public string LibraryName => "KompasTime";
    public string DisplayLibraryName => "KompasTime";
    public string LibraryHelpFile => "";
    public int ProtectNumber => 0;
    public bool IsOnApplication7 => true;
}
