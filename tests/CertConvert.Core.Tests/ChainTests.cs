using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace CertConvert.Core.Tests;

public class ChainTests
{
    [Fact]
    public void Order_SortsShuffledInput_LeafFirst()
    {
        using var chain = TestChain.Create();
        var ordered = ChainTools.Order(chain.Shuffled);

        Assert.Equal(
            new[] { chain.Leaf.Thumbprint, chain.Intermediate.Thumbprint, chain.Root.Thumbprint },
            ordered.Select(c => c.Thumbprint));
    }

    [Fact]
    public void Order_DeduplicatesRepeatedCertificates()
    {
        using var chain = TestChain.Create();
        var ordered = ChainTools.Order([chain.Leaf, chain.Root, chain.Leaf, chain.Intermediate]);
        Assert.Equal(3, ordered.Count);
    }

    [Fact]
    public void Order_RejectsForeignCertificate()
    {
        using var chain = TestChain.Create();
        using var other = TestChain.Create();
        var e = Assert.Throws<CertConvertException>(
            () => ChainTools.Order([chain.Leaf, chain.Intermediate, chain.Root, other.Leaf]));
        Assert.Contains("chain", e.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_FullChain_IsValid()
    {
        using var chain = TestChain.Create();
        var result = ChainTools.Validate(chain.Shuffled);

        Assert.True(result.IsValid,
            string.Join("; ", result.Elements.SelectMany(e => e.Issues).Concat(result.Notes)));
        Assert.Equal(3, result.Elements.Count);
        Assert.All(result.Elements, e => Assert.True(e.IsOk));
    }

    [Fact]
    public void Validate_WithoutRoot_VerifiesAndNotes()
    {
        using var chain = TestChain.Create();
        var result = ChainTools.Validate([chain.Leaf, chain.Intermediate]);

        Assert.True(result.IsValid,
            string.Join("; ", result.Elements.SelectMany(e => e.Issues)));
        Assert.Contains(result.Notes, n => n.Contains("Root certificate not supplied"));
    }

    [Fact]
    public void Validate_ExpiredLeaf_Fails()
    {
        var now = DateTimeOffset.UtcNow;
        using var chain = TestChain.Create(
            leafNotBefore: now.AddYears(-2), leafNotAfter: now.AddYears(-1));
        var result = ChainTools.Validate(chain.All);

        Assert.False(result.IsValid);
        Assert.Contains(result.Elements, e => e.Issues.Any(i => i.Contains("NotTimeValid")));
    }

    [Fact]
    public void Validate_TamperedChain_Fails()
    {
        // Leaf claims to be issued by the intermediate, but a different CA with the
        // same subject name actually signed nothing — signature check must fail.
        using var chain = TestChain.Create();
        using var impostorKey = RSA.Create(2048);
        var impostorReq = new CertificateRequest(
            chain.Intermediate.SubjectName, impostorKey,
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        impostorReq.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(true, false, 0, true));
        var now = DateTimeOffset.UtcNow;
        using var impostor = impostorReq.CreateSelfSigned(now.AddDays(-1), now.AddYears(5));

        var result = ChainTools.Validate([chain.Leaf, impostor]);
        Assert.False(result.IsValid);
    }

    [Fact]
    public void Order_SingleCertificate_ReturnsIt()
    {
        using var chain = TestChain.Create();
        var ordered = ChainTools.Order([chain.Leaf]);
        Assert.Equal(chain.Leaf.Thumbprint, Assert.Single(ordered).Thumbprint);
    }

    [Fact]
    public void Validate_SelfSignedRootSupplied_NotesNotSystemTrusted()
    {
        // Anchoring to a self-signed root from the bundle (no system roots) is a
        // valid chain, but the verdict must say it's not a system-trusted CA.
        using var chain = TestChain.Create();
        var result = ChainTools.Validate(chain.Shuffled);
        Assert.True(result.IsValid,
            string.Join("; ", result.Elements.SelectMany(e => e.Issues)));
        Assert.Contains(result.Notes, n => n.Contains("not a system-trusted CA"));
    }
}
