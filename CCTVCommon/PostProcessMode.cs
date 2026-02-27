namespace CCTVCommon
{
    /// <summary>
    /// Post-processing filter modes applied before ASCII conversion
    /// </summary>
    public enum PostProcessMode
    {
        /// <summary>No post-processing</summary>
        None = 0,
        
        /// <summary>Light 3x3 box blur - smooths pixels, reduces harsh edges (~2-3ms)</summary>
        LightBlur = 1,
        
        /// <summary>Medium 5x5 box blur - more smoothing (~5-7ms)</summary>
        MediumBlur = 2,
        
        /// <summary>Sharpen edges - enhances detail (~3-5ms)</summary>
        Sharpen = 3
    }
}
