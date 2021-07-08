using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using Mapsui.Fetcher;
using Microsoft.Win32;
using Point = Mapsui.Geometries.Point;

namespace SlideLibrary.Demo
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml.
    /// </summary>
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
            DataContext = this;
        }

        private double resolution = 0;
        /// <summary>
        /// Resolution(um/pixel).
        /// </summary>
        public double Resolution
        {
            get { return resolution; }
            set { SetProperty(ref resolution, value); }
        }

        public ObservableCollection<KeyValuePair<string, object>> Info { get; } = new ObservableCollection<KeyValuePair<string, object>>();
        public ObservableCollection<KeyValuePair<string, ImageSource>> Images { get; } = new ObservableCollection<KeyValuePair<string, ImageSource>>();


        private ISlideSource _slideSource;
        private Random random = new Random();

        /// <summary>
        /// Open slide file
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void Open_Click(object sender, RoutedEventArgs e)
        {
            OpenFileDialog openFileDialog = new OpenFileDialog();
            if (openFileDialog.ShowDialog() == true)
            {
                var file = openFileDialog.FileName;
                if (_slideSource != null) (_slideSource as IDisposable).Dispose();
                _slideSource = SlideSourceBase.Create(file);
                if (_slideSource == null) return;
                InitMain(_slideSource);
                InitPreview(_slideSource);
                InitImage(_slideSource);
                InitInfo(_slideSource);
                

            }
        }

        /// <summary>
        /// Init main map
        /// </summary>
        /// <param name="_slideSource"></param>
        private void InitMain(ISlideSource _slideSource)
        {
            MainMap.Map.Layers.Clear();
            MainMap.Map.Layers.Add(new SlideTileLayer(_slideSource, dataFetchStrategy: new MinimalDataFetchStrategy()));
            MainMap.Map.Layers.Add(new SlideSliceLayer(_slideSource) { Enabled = false, Opacity = 0.5 });
        }

        /// <summary>
        /// Init hawkeye map
        /// </summary>
        /// <param name="source"></param>
        private void InitPreview(ISlideSource source)
        {
            PreviewMap.Map.PanLock = false;
            PreviewMap.Map.RotationLock = false;
            PreviewMap.Map.ZoomLock = false;
            PreviewMap.Map.Layers.Clear();
            PreviewMap.Map.Layers.Add(new SlideTileLayer(source, dataFetchStrategy: new MinimalDataFetchStrategy()));
            PreviewMap.Navigator.NavigateToFullEnvelope(Mapsui.Utilities.ScaleMethod.Fit);
            PreviewMap.Map.PanLock = true;
            PreviewMap.Map.RotationLock = true;
            PreviewMap.Map.ZoomLock = true;
        }

        /// <summary>
        /// Init label etc.
        /// </summary>
        /// <param name="slideSource"></param>
        private void InitImage(ISlideSource slideSource)
        {
            foreach (var item in slideSource.GetExternImages())
            {
                Images.Add(new KeyValuePair<string, ImageSource>(item.Key, (ImageSource)new ImageSourceConverter().ConvertFrom(item.Value)));
            }
        }

        /// <summary>
        /// Init info list.
        /// </summary>
        /// <param name="slideSource"></param>
        private void InitInfo(ISlideSource slideSource)
        {
            Title = slideSource.Source;
            Info.Clear();
            foreach (var item in slideSource.ExternInfo)
            {
                Info.Add(item);
            }
            Info.Add(new KeyValuePair<string, object>("--Layer--", "-Resolution(um/pixel)-"));
            foreach (var item in slideSource.Schema.Resolutions)
            {
                Info.Add(new KeyValuePair<string, object>(item.Key.ToString(), item.Value.UnitsPerPixel));
            }
            Resolution = slideSource.MinUnitsPerPixel;
            LayerList.ItemsSource = MainMap.Map.Layers.Reverse();
        }

        /// <summary>
        /// Random goto.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void Random_Click(object sender, RoutedEventArgs e)
        {
            var w = random.Next(0, (int)_slideSource.Schema.Extent.Width);
            var h = random.Next(-(int)_slideSource.Schema.Extent.Height, 0);
            MainMap.Navigator.NavigateTo(new Point(w, h), MainMap.Viewport.Resolution);
        }

        /// <summary>
        /// Open process directory.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void Explorer_Click(object sender, RoutedEventArgs e)
        {
            Process.Start("explorer.exe", LibraryInfo.AssemblyDirectory);
        }
    }

    /// <summary>
    /// Bindable
    /// </summary>
    public partial class MainWindow : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        protected virtual bool SetProperty<T>(ref T storage, T value, [CallerMemberName] string propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(storage, value))
            {
                return false;
            }

            storage = value;
            RaisePropertyChanged(propertyName);
            return true;
        }


        protected virtual bool SetProperty<T>(ref T storage, T value, Action onChanged, [CallerMemberName] string propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(storage, value))
            {
                return false;
            }

            storage = value;
            onChanged?.Invoke();
            RaisePropertyChanged(propertyName);
            return true;
        }

        protected void RaisePropertyChanged([CallerMemberName] string propertyName = null)
        {
            OnPropertyChanged(new PropertyChangedEventArgs(propertyName));
        }

        protected virtual void OnPropertyChanged(PropertyChangedEventArgs args)
        {
            this.PropertyChanged?.Invoke(this, args);
        }
    }
}

