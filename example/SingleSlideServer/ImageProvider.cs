using System;
using Microsoft.Extensions.Options;
using OpenSlideSharp;

namespace SingleSlideServer
{
    public class ImageProvider : IDisposable
    {
        private DeepZoomGenerator _generator;

        public ImageProvider(IOptions<ImageOption> options)
        {
            _generator = new DeepZoomGenerator(OpenSlideImage.Open(options.Value.Path), tileSize: 254, overlap: 1);
        }

        public DeepZoomGenerator DeepZoomGenerator => _generator;

        private bool disposedValue;
        protected virtual void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                if (disposing)
                {
                    _generator.Dispose();
                }

                disposedValue = true;
            }
        }

        public void Dispose()
        {
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }
    }
}
