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
    public OptiScalerPreset Preset { get; private set; } = OptiScalerPreset.Balanced;

    // Index 0 keeps the preset's own tier; the rest map onto OptiScalerScaling in order.
    public string[] ScalingChoices { get; } = new[] { "Preset default" }.Concat(OptiScalerScalings.Names).ToArray();
    public OptiScalerScaling? Scaling { get; private set; }

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
        var presetRadio = defaultPreset switch
        {
            OptiScalerPreset.Light => LightRadio,
            OptiScalerPreset.Detail => DetailRadio,
            OptiScalerPreset.Max => MaxRadio,
            _ => BalancedRadio
        };
        presetRadio.IsChecked = true;
        ScalingCombo.SelectedIndex = 0;
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        Route = PreSrRadio.IsChecked == true ? InstallRoute.OptiScalerPreSr : InstallRoute.PostFsrRuntime;
        Scaling = ScalingCombo.SelectedIndex <= 0 ? null : (OptiScalerScaling)(ScalingCombo.SelectedIndex - 1);
        Preset = LightRadio.IsChecked == true ? OptiScalerPreset.Light
            : DetailRadio.IsChecked == true ? OptiScalerPreset.Detail
            : MaxRadio.IsChecked == true ? OptiScalerPreset.Max
            : OptiScalerPreset.Balanced;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
