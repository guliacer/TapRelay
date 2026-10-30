using System;

namespace TapRelay
{
    /// <summary>
    /// PC 端界面令牌。集中保存会影响跨卡片一致性的尺寸与字体，
    /// 避免新增控件时重新散落一组近似但不相同的常量。
    /// </summary>
    internal static class UiTokens
    {
        public const string UiFontFamily = "Microsoft YaHei UI";
        public const string MonoFontFamily = "Consolas";

        public const float BodyFontSize = 9.5F;
        public const float BodyLargeFontSize = 10F;
        public const float SectionTitleFontSize = 10.5F;
        public const float CaptionFontSize = 8.5F;
        public const float LogFontSize = 9.5F;
        public const float LogPreviewFontSize = 9F;
        public const float AppNameFontSize = 8.5F;

        public const int PageMargin = 24;
        public const int CardPadding = 20;
        public const int Gap = 8;
        public const int SectionGap = 16;
        public const int RowGap = 12;

        public const int MainButtonHeight = 40;
        // 单行测试通知与地址框共用主输入高度，保证两行字段与右侧按钮对齐。
        public const int MessageHeight = MainButtonHeight;
        public const int StatusStripHeight = 56;
        public const int StatusStripPadding = 14;
        public const int IconButtonSize = 28;
        public const int IconButtonGap = 6;

        public const int CardRadius = 14;
        public const int StatusStripRadius = 14;
        public const int ButtonRadius = 10;
        public const int FieldRadius = 10;
        public const int ChipRadius = 10;

        public const int EmptyStateHeight = 40;
    }
}
