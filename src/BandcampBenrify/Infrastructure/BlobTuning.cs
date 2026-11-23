namespace BandcampBenrify.Infrastructure;

/// <summary>
/// ビジュアライザの「柔らかい輪郭」の調整値。初期値が正式版で使う値。
/// 初期値は 2026-11-23 に dev の調整パネルで決めた値 (調整用 UI の「記録した値に戻す」でこの値に戻る)。
/// それより前 (最初に「だいぶいい感じ」となった時点) の値: Threshold 0.42, Curve 1.4, BandWave [26, 26, 22, 18, 14],
/// KickSwell 48, BassSwell 14, DriftSpeed 0.35, LayerGap 16, AlphaLevel 0.2, AlphaPunch 0.35 (ほかは今と同じ)。
/// </summary>
public sealed class BlobTuning
{
    // ---- 音の拾い方 ----

    /// <summary>これより小さい音量 (0〜1) は無いものとして扱う。</summary>
    public double Threshold { get; set; } = 0.49;

    /// <summary>音量の強調 (大きいほど、大きい音だけが目立つ)。</summary>
    public double Curve { get; set; } = 2.18;

    /// <summary>音量が下がる速さ (毎秒)。</summary>
    public double LevelDecay { get; set; } = 2.2;

    /// <summary>立ち上がりを測るための、直前の平均をとる時間 (秒)。</summary>
    public double AverageSeconds { get; set; } = 0.4;

    /// <summary>
    /// 「重力」の基準の BPM。曲の BPM がこれより速いほど、ふくらみや弾けが速く戻る (速い曲でも拍ごとの鼓動が見えるように)。
    /// 遅い曲では逆に少しゆっくり戻る。BPM が分からない曲は基準どおり。
    /// </summary>
    public double GravityReferenceBpm { get; set; } = 120;

    /// <summary>BPM による重力の効き方 (0 で BPM に関係なし、1 で BPM に比例、それ以上はもっと強く)。</summary>
    public double GravityStrength { get; set; } = 1;

    /// <summary>
    /// 重力で「鳴り続けている分」(ずっと鳴っているベースなどでのふくらみ・波) を抑える強さ。
    /// 鳴り続けの分を 1 / 重力^この値 にする (基準より遅い曲では抑えない)。速い曲でもキックの弾けが見えるように。
    /// </summary>
    public double GravitySustainDamping { get; set; } = 2;

    /// <summary>
    /// 抑えた「鳴り続けの分」を、音の立ち上がり (キックなど) の瞬間に元の大きさへ戻すときの基準。
    /// 立ち上がりがこの値以上なら元の大きさ (小さいほど、小さな立ち上がりでも面積がしっかり出る)。
    /// </summary>
    public double SustainHitReference { get; set; } = 0.1;

    /// <summary>立ち上がり (弾け) が消える速さ (毎秒)。</summary>
    public double PunchDecay { get; set; } = 6;

    // ---- 形 ----

    /// <summary>鳴り続けている音で出る波の大きさ (倍率)。</summary>
    public double SustainWave { get; set; } = 0.2;

    /// <summary>音の立ち上がりで出る波の大きさ (倍率)。</summary>
    public double PunchWave { get; set; } = 1.2;

    /// <summary>音域ごとの波の大きさ (低音 → 高音。山の数は 2, 3, 4, 5, 7)。</summary>
    public double[] BandWave { get; set; } = [26, 16.5, 16.8, 16.3, 56];

    /// <summary>キック (低音の立ち上がり) で全体がふくらむ大きさ。</summary>
    public double KickSwell { get; set; } = 81.6;

    /// <summary>低音が鳴っている間に全体がふくらむ大きさ。</summary>
    public double BassSwell { get; set; } = 46.8;

    /// <summary>普段のうねりの速さ。</summary>
    public double DriftSpeed { get; set; } = 0.35;

    /// <summary>静かなときのかすかな揺れの大きさ。</summary>
    public double IdleWobble { get; set; } = 3;

    /// <summary>ジャケットの縁から、いちばん内側の層までの余白。</summary>
    public double BaseGap { get; set; } = 22;

    /// <summary>層と層の間隔。</summary>
    public double LayerGap { get; set; } = 20.2;

    // ---- 色 ----

    /// <summary>層の濃さ (音が無いとき)。</summary>
    public double AlphaBase { get; set; } = 0.24;

    /// <summary>鳴っている間に濃くなる分。</summary>
    public double AlphaLevel { get; set; } = 0.58;

    /// <summary>立ち上がりで濃くなる分。</summary>
    public double AlphaPunch { get; set; } = 0.57;

    /// <summary>層ごとの色相の広がり (度)。ジャケットの色を中心に振る。</summary>
    public double HueSpread { get; set; } = 160;

    /// <summary>色相がひと回りする秒数。</summary>
    public double HueCycleSeconds { get; set; } = 120;
}
