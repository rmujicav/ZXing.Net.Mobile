namespace ZXing.Mobile
{
    public struct FocusPointOfInterest
    {
        private float? x;
        private float? y;

        public float X
        {
            get => x ?? .5f;
            set => x = value;
        }

        public float Y
        {
            get => y ?? .5f;
            set => y = value;
        }

        public FocusPointOfInterest(float x, float y)
        {
            this.x = x;
            this.y = y;
        }
    }
}
