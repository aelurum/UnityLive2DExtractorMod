namespace UnityLive2DExtractor
{
    public sealed class Live2DExtractorOptions
    {
        public Live2DMotionMode MotionMode { get; set; } = Live2DMotionMode.MonoBehaviour;
        public bool ForceBezier { get; set; }
        public bool SearchByFilename { get; set; }
        public bool FilterTextures { get; set; }
    }
}
