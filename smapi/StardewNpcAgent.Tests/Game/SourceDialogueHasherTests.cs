using StardewNpcAgent.Game;

namespace StardewNpcAgent.Tests.Game;

/// <summary>
/// 验证 source hash 是逐字符 UTF-8 SHA-256 指纹，不做任何隐式文本规范化。
/// </summary>
public sealed class SourceDialogueHasherTests
{
    /// <summary>
    /// 使用外部 SHA-256 工具预先计算的 Unicode 向量，证明编码固定为 UTF-8。
    /// </summary>
    [Fact]
    public void Compute_UsesUtf8Sha256WithCanonicalPrefix()
    {
        string result = SourceDialogueHasher.Compute("你好，阿比盖尔");

        Assert.Equal(
            "sha256:a12cc9b2412a9b1d9c6833d77b9564e31e5f5768903b9cec68f7d1dd4025efd0",
            result);
    }

    /// <summary>
    /// CRLF 与 LF 是不同源文本，绝不能为了便利而统一换行。
    /// </summary>
    [Fact]
    public void Compute_DistinguishesCrLfFromLf()
    {
        string crlf = SourceDialogueHasher.Compute("第一行\r\n第二行");
        string lf = SourceDialogueHasher.Compute("第一行\n第二行");

        Assert.Equal("sha256:ebf2d2d3b034853c183c5820f24df083cd02c77ca9e7b2de9e390a1bcaf06008", crlf);
        Assert.Equal("sha256:61f8655a9b219b9ce122c6c7cd59ad09feac38240699cfec8d0c9b1e81a748ab", lf);
        Assert.NotEqual(crlf, lf);
    }

    /// <summary>
    /// 单字符变化必须改变指纹，供资产应用时做最后一次 source 一致性检查。
    /// </summary>
    [Fact]
    public void Compute_ChangesWhenOneCharacterChanges()
    {
        Assert.NotEqual(
            SourceDialogueHasher.Compute("今天去湖边。"),
            SourceDialogueHasher.Compute("今天去湖边！"));
    }

    /// <summary>
    /// null 不是可哈希源文本，调用方必须显式处理 source missing。
    /// </summary>
    [Fact]
    public void Compute_RejectsNull()
    {
        Assert.Throws<ArgumentNullException>(() => SourceDialogueHasher.Compute(null!));
    }
}
