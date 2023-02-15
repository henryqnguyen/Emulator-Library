# Emulator-Library

```xaml
<Window x:Class="GPR.MainWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="GPR" Height="250" Width="350">
    <Grid Margin="10">
        <Grid.RowDefinitions>
            <RowDefinition Height="*" />
            <RowDefinition Height="auto" />
        </Grid.RowDefinitions>

        <TextBlock x:Name="debugTextBlock" Grid.Row="0" TextWrapping="Wrap" />

        <StackPanel Grid.Row="1" Orientation="Horizontal">
            <Button x:Name="listenButton" Content="Listen" Margin="0,0,10,0" Width="100" Click="listenButton_Click" />
            <Button x:Name="stopButton" Content="Stop" Width="100" Click="stopButton_Click" />
        </StackPanel>

    </Grid>
</Window>

```
