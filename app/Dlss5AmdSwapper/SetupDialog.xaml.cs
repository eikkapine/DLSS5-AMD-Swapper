using System.Windows;
using Dlss5AmdSwapper.Models;

namespace Dlss5AmdSwapper;

public partial class SetupDialog : Window
{
    public string GameName { get; }
    public string PackageSummary { get; }
    public string WeightsSummary { get; }
    public string ActivationGuidance { get; }
    public bool PreSrAvailable { get; }
    public InstallRoute Route { get; private set; } = InstallRoute.None;
    public OptiScalerPreset Preset { get; private set; } = OptiScalerPreset.Quality;

    public SetupDialog(string gameName, string packageSummary, string weightsSummary, bool preSrAvailable, OptiScalerPreset defaultPreset,
        string activationGuidance = "Enable a supported temporal upscaler input, then open the OptiScaler menu with Del. Refresh evidence after entering gameplay.")
    {
        GameName = gameName;
        PackageSummary = packageSummary;
        WeightsSummary = weightsSummary;
        ActivationGuidance = activationGuidance;
        PreSrAvailable = preSrAvailable;
        InitializeComponent();
        DataContext = this;
        (preSrAvailable ? PreSrRadio : PostFsrRadio).IsChecked = true;
        (defaultPreset == OptiScalerPreset.Performance ? PerformanceRadio : QualityRadio).IsChecked = true;
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        Route = PreSrRadio.IsChecked == true ? InstallRoute.OptiScalerPreSr : InstallRoute.PostFsrRuntime;
        Preset = PerformanceRadio.IsChecked == true ? OptiScalerPreset.Performance : OptiScalerPreset.Quality;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
