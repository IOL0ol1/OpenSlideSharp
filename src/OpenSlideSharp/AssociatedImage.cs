namespace OpenSlideSharp
{
    /// <summary>
    /// Associated image
    /// </summary>
    public class AssociatedImage
    {
        /// <summary>
        /// 
        /// </summary>
        /// <param name="dimensions"></param>
        /// <param name="data"></param>
        public AssociatedImage(ImageDimension dimensions, byte[] data)
        {
            Dimensions = dimensions;
            Data = data;
        }

        /// <summary>
        /// Associated image dimensions
        /// </summary>
        public ImageDimension Dimensions { get; private set; }

        /// <summary>
        /// Associated image ARGB data
        /// </summary>
        public byte[] Data { get; private set; }
    }
}
