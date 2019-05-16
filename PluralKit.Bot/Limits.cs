namespace PluralKit.Bot {
    public static class Limits {
        public static readonly int MaxSystemNameLength = 100;
        public static readonly int MaxSystemTagLength = 31;
        public static readonly int MaxDescriptionLength = 1000;
        public static readonly int MaxMemberNameLength = 50;
        public static readonly int MaxPronounsLength = 100;

        public static readonly long AvatarFileSizeLimit = 1024 * 1024;
        public static readonly int AvatarDimensionLimit = 1000;
    }
}