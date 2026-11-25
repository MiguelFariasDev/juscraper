using Juscraper.Cnj;

namespace Juscraper.Tests;

public sealed class CnjTests
{
    [Theory]
    [InlineData("0000248-42.2024.8.26.0319", "TJSP")]
    [InlineData("00002484220248260319", "TJSP")]
    [InlineData("0700482-85.2026.8.02.0349", "TJAL")]
    [InlineData("0002221-74.2006.8.07.0001", "TJDFT")]
    public void Aceita_com_e_sem_mascara_e_resolve_o_tribunal(string entrada, string tribunal)
    {
        var numero = NumeroCnj.Parse(entrada);

        Assert.Equal(tribunal, numero.Tribunal);
        Assert.Equal(20, numero.Digitos.Length);
        Assert.Equal(numero, NumeroCnj.Parse(numero.Formatado));
    }

    [Fact]
    public void Partes_do_numero()
    {
        var numero = NumeroCnj.Parse("0000248-42.2024.8.26.0319");

        Assert.Equal("0000248-42.2024", numero.NumeroDigitoAno);
        Assert.Equal(("2024", 8, "26", "0319"), (numero.Ano, numero.Segmento, numero.Tr, numero.Origem));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("123")]
    [InlineData("0000248-43.2024.8.26.0319")] // DV errado
    [InlineData("0000248-42.2024.0.26.0319")] // segmento 0 não existe
    [InlineData("5018258-87.2026.8.13.0439")] // dígito trocado
    [InlineData("0000248-42.2024.8.26.03190")] // 21 dígitos
    public void Rejeita_invalidos(string? entrada)
    {
        Assert.Throws<NumeroCnjInvalidoException>(() => NumeroCnj.Parse(entrada));
        Assert.False(NumeroCnj.Valido(entrada));
    }

    [Fact]
    public void Calcula_dv_igual_ao_numero_real() =>
        Assert.Equal("42", NumeroCnj.CalcularDv("0000248", "2024", 8, "26", "0319"));

    [Theory]
    [InlineData(8, "07", "TJDFT")]
    [InlineData(8, "27", "TJTO")]
    [InlineData(8, "28", null)]
    [InlineData(4, "06", "TRF6")]
    [InlineData(5, "15", "TRT15")]
    [InlineData(6, "00", "TSE")]
    [InlineData(6, "26", "TRE-SP")]
    [InlineData(9, "13", "TJMMG")]
    [InlineData(9, "01", null)]
    [InlineData(1, "00", "STF")]
    public void Resolve_tribunal(int segmento, string tr, string? esperado) =>
        Assert.Equal(esperado, TribunalResolver.Resolver(segmento, tr));

    [Theory]
    [InlineData("Reqte", Polo.Ativo)]
    [InlineData("Reqdo", Polo.Passivo)]
    [InlineData("Ré", Polo.Passivo)]
    [InlineData("Autor do Fato", Polo.Passivo)]
    [InlineData("Apelante:", Polo.Ativo)]
    [InlineData("Requerente", Polo.Ativo)]
    [InlineData("Recorrente", Polo.Ativo)]
    [InlineData("Interessado", Polo.Outros)]
    public void Infere_o_polo(string tipo, Polo esperado) => Assert.Equal(esperado, Normalizacao.InferirPolo(tipo));
}
