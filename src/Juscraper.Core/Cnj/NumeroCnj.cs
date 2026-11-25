using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Juscraper.Cnj;

/// <summary>
/// Número único de processo no padrão CNJ (Resolução CNJ nº 65/2008):
/// <c>NNNNNNN-DD.AAAA.J.TR.OOOO</c>.
///
/// Espelha <c>CNJNumber</c> e <c>TribunalResolver</c> do Iustitia (Legal.Domain.Shared), para os
/// dois lados concordarem sobre o que é um número válido e a qual tribunal ele pertence.
/// Aceita a entrada com ou sem máscara e confere os dígitos verificadores (mod 97, ISO 7064).
/// </summary>
public sealed record NumeroCnj : IParsable<NumeroCnj>
{
    private NumeroCnj(string digitos) => Digitos = digitos;

    /// <summary>Os 20 dígitos, sem separadores.</summary>
    public string Digitos { get; }

    public string Sequencial => Digitos[..7];

    public string Dv => Digitos[7..9];

    public string Ano => Digitos[9..13];

    public int Segmento => Digitos[13] - '0';

    public string Tr => Digitos[14..16];

    public string Origem => Digitos[16..20];

    /// <summary>Forma canônica mascarada.</summary>
    public string Formatado => $"{Digitos[..7]}-{Digitos[7..9]}.{Digitos[9..13]}.{Digitos[13]}.{Digitos[14..16]}.{Digitos[16..20]}";

    /// <summary><c>NNNNNNN-DD.AAAA</c> — o primeiro campo do formulário do e-SAJ.</summary>
    public string NumeroDigitoAno => Formatado[..15];

    /// <summary>Sigla do tribunal (ex.: TJSP), ou <c>null</c> se J.TR não aponta para nenhum.</summary>
    public string? Tribunal => TribunalResolver.Resolver(Segmento, Tr);

    public static NumeroCnj Parse(string? s, IFormatProvider? provider = null) =>
        TryParse(s, provider, out var numero) ? numero : throw new NumeroCnjInvalidoException(s);

    public static bool TryParse([NotNullWhen(true)] string? s, IFormatProvider? provider, [MaybeNullWhen(false)] out NumeroCnj result)
    {
        result = null;
        if (string.IsNullOrWhiteSpace(s))
        {
            return false;
        }

        Span<char> buffer = stackalloc char[20];
        var tamanho = 0;
        foreach (var c in s)
        {
            if (!char.IsAsciiDigit(c))
            {
                continue;
            }

            if (tamanho == 20)
            {
                return false;
            }

            buffer[tamanho++] = c;
        }

        // Segmento 0 não existe (art. 1º, §1º, V da Resolução 65/2008).
        if (tamanho != 20 || buffer[13] == '0' || !DvValido(buffer))
        {
            return false;
        }

        result = new NumeroCnj(new string(buffer));
        return true;
    }

    public static bool Valido(string? s) => TryParse(s, null, out _);

    /// <summary>Calcula os dígitos verificadores — útil em testes e para montar números.</summary>
    public static string CalcularDv(string sequencial, string ano, int segmento, string tr, string origem)
    {
        var baseNumerica = $"{sequencial.PadLeft(7, '0')}{ano}{segmento}{tr.PadLeft(2, '0')}{origem.PadLeft(4, '0')}00";
        var resto = (int)(UInt128.Parse(baseNumerica, CultureInfo.InvariantCulture) % 97);
        return (98 - resto).ToString("00", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Com os DV removidos e "00" ao final, o resto por 97 deve ser 98 − DD. O número tem 22
    /// dígitos: não cabe em <c>ulong</c>, cabe folgado em <see cref="UInt128"/>.
    /// </summary>
    private static bool DvValido(ReadOnlySpan<char> digitos)
    {
        Span<char> semDv = stackalloc char[20];
        digitos[..7].CopyTo(semDv);
        digitos[9..].CopyTo(semDv[7..]);
        semDv[18] = '0';
        semDv[19] = '0';

        var resto = (int)(UInt128.Parse(semDv, CultureInfo.InvariantCulture) % 97);
        var dv = (digitos[7] - '0') * 10 + (digitos[8] - '0');
        return 98 - resto == dv;
    }

    public override string ToString() => Formatado;
}

public static class TribunalResolver
{
    /// <summary>UFs na ordem dos códigos TR 01..27.</summary>
    private static readonly string[] Ufs =
    [
        "AC", "AL", "AP", "AM", "BA", "CE", "DFT", "ES", "GO", "MA", "MT", "MS", "MG",
        "PA", "PB", "PR", "PE", "PI", "RJ", "RN", "RS", "RO", "RR", "SC", "SE", "SP", "TO",
    ];

    /// <summary>Justiça Militar Estadual existe em três estados apenas (art. 125, §3º, CF).</summary>
    private static readonly Dictionary<int, string> MilitaresEstaduais = new() { [13] = "TJMMG", [21] = "TJMRS", [26] = "TJMSP" };

    public static string? Resolver(int segmento, string tr)
    {
        if (!int.TryParse(tr, NumberStyles.None, CultureInfo.InvariantCulture, out var codigo))
        {
            return null;
        }

        return segmento switch
        {
            1 => "STF",
            2 => "CNJ",
            3 => "STJ",
            4 => codigo is >= 1 and <= 6 ? $"TRF{codigo}" : null,
            5 => codigo is >= 1 and <= 24 ? $"TRT{codigo}" : null,
            6 => codigo == 0 ? "TSE" : Uf(codigo) is { } ufEleitoral ? $"TRE-{ufEleitoral}" : null,
            7 => "STM",
            // O tribunal do DF é TJDFT: a sigla inclui os Territórios.
            8 => Uf(codigo) is { } uf ? $"TJ{uf}" : null,
            9 => MilitaresEstaduais.GetValueOrDefault(codigo),
            _ => null,
        };
    }

    private static string? Uf(int codigo) => codigo >= 1 && codigo <= Ufs.Length ? Ufs[codigo - 1] : null;
}
