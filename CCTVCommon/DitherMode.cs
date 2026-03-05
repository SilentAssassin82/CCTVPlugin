namespace CCTVCommon
{
    /// <summary>
    /// Dithering algorithm applied during colour/grayscale quantisation.
    /// </summary>
    public enum DitherMode
    {
        /// <summary>No dithering — straight quantisation</summary>
        None = 0,

        /// <summary>4×4 Bayer ordered dithering — temporally stable, no flicker</summary>
        Bayer = 1,

        /// <summary>Floyd-Steinberg error-diffusion dithering — smoother gradients, may flicker on video</summary>
        FloydSteinberg = 2
    }
}
