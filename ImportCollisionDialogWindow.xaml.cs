using System.Windows;

namespace MakroWerkstatt;

public partial class ImportCollisionDialogWindow : Window
{
    public ImportCollisionAction Action { get; private set; } = ImportCollisionAction.Skip;

    public ImportCollisionDialogWindow() => InitializeComponent();

    private void Skip_Click(object sender, RoutedEventArgs e) => Finish(ImportCollisionAction.Skip);
    private void Copy_Click(object sender, RoutedEventArgs e) => Finish(ImportCollisionAction.Copy);
    private void Overwrite_Click(object sender, RoutedEventArgs e) => Finish(ImportCollisionAction.Overwrite);

    private void Finish(ImportCollisionAction action)
    {
        Action = action;
        DialogResult = true;
    }
}
