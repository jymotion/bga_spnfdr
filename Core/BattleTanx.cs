using System.Drawing;

namespace BgaSpnfdr
{
    /// <summary>Facts about BattleTanx: Global Assault's score display. Shared
    /// by every platform's build.</summary>
    internal static class BattleTanx
    {
        // Where the score sits inside the full game picture, as fractions of
        // the crop: x 0–35%, y 89–94% (tuned against the real feed).
        public static readonly RectangleF ScoreFraction = new RectangleF(0f, 0.89f, 0.35f, 0.05f);
        public const int ScoreMultiple = 25;    // scores always move in 25s
        public const int MaxScoreDigits = 6;    // scores never reach 1,000,000
        public const long MaxScoreJump = 50000; // bigger forward jumps are misreads
    }
}
