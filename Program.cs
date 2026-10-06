using System;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Markup;

namespace XadoSpec
{
    static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            string dump = Arg(args, "--dump"), shot = Arg(args, "--shot");
            if (dump != null)
            {
                File.WriteAllText(dump, Collector.Collect().ToText(), new UTF8Encoding(true));
                return 0;
            }

            var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
            app.Resources = (ResourceDictionary)XamlReader.Parse(Theme.Xaml);
            if (shot == null)
                app.DispatcherUnhandledException += (s, e) =>
                {
                    MessageBox.Show(e.Exception.Message, "XadoSpec", MessageBoxButton.OK, MessageBoxImage.Error);
                    e.Handled = true;
                };
            double.TryParse(Arg(args, "--w"), out double w);
            double.TryParse(Arg(args, "--h"), out double h);
            app.Run(new MainWindow(shot, w > 0 ? w : 1180, h > 0 ? h : 2400));
            return 0;
        }

        static string Arg(string[] args, string name)
        {
            int i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }
    }

    static class Theme
    {
        public const string Xaml = @"
<ResourceDictionary xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'
                    xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'>
  <Style x:Key='Btn' TargetType='Button'>
    <Setter Property='Foreground' Value='#E8EBF1'/>
    <Setter Property='Background' Value='#1B1F28'/>
    <Setter Property='BorderBrush' Value='#2A303D'/>
    <Setter Property='Padding' Value='16,9'/>
    <Setter Property='FontSize' Value='13'/>
    <Setter Property='Cursor' Value='Hand'/>
    <Setter Property='FocusVisualStyle' Value='{x:Null}'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='Button'>
          <Border x:Name='b' Background='{TemplateBinding Background}' BorderBrush='{TemplateBinding BorderBrush}'
                  BorderThickness='1' CornerRadius='10' Padding='{TemplateBinding Padding}'>
            <ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property='IsMouseOver' Value='True'><Setter TargetName='b' Property='Opacity' Value='0.86'/></Trigger>
            <Trigger Property='IsPressed' Value='True'><Setter TargetName='b' Property='Opacity' Value='0.7'/></Trigger>
            <Trigger Property='IsEnabled' Value='False'><Setter TargetName='b' Property='Opacity' Value='0.45'/></Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>
  <Style x:Key='BtnAccent' TargetType='Button' BasedOn='{StaticResource Btn}'>
    <Setter Property='Background' Value='#4C6FFF'/>
    <Setter Property='BorderBrush' Value='#5B7BFF'/>
    <Setter Property='Foreground' Value='White'/>
  </Style>
  <Style TargetType='ScrollBar'>
    <Setter Property='Width' Value='12'/>
    <Setter Property='MinWidth' Value='12'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='ScrollBar'>
          <Border Background='Transparent'>
            <Track x:Name='PART_Track' IsDirectionReversed='True' Orientation='Vertical'>
              <Track.Thumb>
                <Thumb>
                  <Thumb.Template>
                    <ControlTemplate TargetType='Thumb'>
                      <Border x:Name='t' Background='#2B3140' CornerRadius='4' Margin='3,2'/>
                      <ControlTemplate.Triggers>
                        <Trigger Property='IsMouseOver' Value='True'><Setter TargetName='t' Property='Background' Value='#3A4254'/></Trigger>
                      </ControlTemplate.Triggers>
                    </ControlTemplate>
                  </Thumb.Template>
                </Thumb>
              </Track.Thumb>
            </Track>
          </Border>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>
  <Style TargetType='ToolTip'>
    <Setter Property='Background' Value='#1F2430'/>
    <Setter Property='Foreground' Value='#E8EBF1'/>
    <Setter Property='BorderBrush' Value='#2E3442'/>
    <Setter Property='Padding' Value='10,6'/>
  </Style>
</ResourceDictionary>";
    }
}
