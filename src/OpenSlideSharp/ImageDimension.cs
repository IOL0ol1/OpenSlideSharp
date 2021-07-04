namespace OpenSlideSharp
{
    /// <summary>
    /// Represents the image dimensions
    /// </summary>
    public struct ImageDimension
    {
        internal long width;
        internal long height;

        /// <summary>
        /// The width of the image.
        /// </summary>
        public long Width => width;

        /// <summary>
        /// The height of the image.
        /// </summary>
        public long Height => height;

        /// <summary>
        /// Initialize a new <see cref="ImageDimension"/> struct.
        /// </summary>
        /// <param name="width">The width of the image.</param>
        /// <param name="height">The height of the image.</param>
        public ImageDimension(long width = -1, long height = -1)
        {
            this.width = width;
            this.height = height;
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="width"></param>
        /// <param name="height"></param>
        public void Deconstruct(out long width, out long height)
        {
            width = this.width;
            height = this.height;
        }

        /// <summary>
        /// 
        /// </summary>
        /// <returns></returns>
        public override string ToString()
        {
            return $"Width:{Width} Height:{Height}";
        }
    }
}
