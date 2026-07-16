using System.Globalization;
using Avalonia.Data;
using CertConvert.Converters;
using CertConvert.ViewModels;

namespace CertConvert.Gui.Tests;

public class EnumBoolConverterTests
{
    private static readonly EnumBoolConverter C = EnumBoolConverter.Instance;

    [Theory]
    [InlineData(CertOutput.SelfSigned, "SelfSigned", true)]
    [InlineData(CertOutput.SelfSigned, "Csr", false)]
    [InlineData(CertOutput.Csr, "Csr", true)]
    public void Convert_MatchesEnumNameToParameter(CertOutput value, string param, bool expected) =>
        Assert.Equal(expected, C.Convert(value, typeof(bool), param, CultureInfo.InvariantCulture));

    [Fact]
    public void Convert_NullValueOrParameter_IsFalse()
    {
        Assert.Equal(false, C.Convert(null, typeof(bool), "Csr", CultureInfo.InvariantCulture));
        Assert.Equal(false, C.Convert(CertOutput.Csr, typeof(bool), null, CultureInfo.InvariantCulture));
    }

    [Fact]
    public void ConvertBack_Checked_ParsesEnum() =>
        Assert.Equal(CertOutput.Csr,
            C.ConvertBack(true, typeof(CertOutput), "Csr", CultureInfo.InvariantCulture));

    [Fact]
    public void ConvertBack_UncheckedOrNull_DoesNothing()
    {
        // The unchecking radio must not clobber the property the checking one set.
        Assert.Equal(BindingOperations.DoNothing,
            C.ConvertBack(false, typeof(CertOutput), "Csr", CultureInfo.InvariantCulture));
        Assert.Equal(BindingOperations.DoNothing,
            C.ConvertBack(null, typeof(CertOutput), "Csr", CultureInfo.InvariantCulture));
        Assert.Equal(BindingOperations.DoNothing,
            C.ConvertBack(true, typeof(CertOutput), null, CultureInfo.InvariantCulture));
    }
}
