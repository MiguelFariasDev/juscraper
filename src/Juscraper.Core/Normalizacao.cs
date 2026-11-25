using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Juscraper;

/// <summary>Normalização de texto e datas vindos do HTML/JSON dos portais (membros de extensão do C# 14).</summary>
public static partial class Normalizacao
{
    public static readonly TimeZoneInfo FusoBrasilia = TimeZoneInfo.FindSystemTimeZoneById("America/Sao_Paulo");

    extension(string? texto)
    {
        /// <summary>Colapsa espaços (incluindo &amp;nbsp;) e devolve <c>null</c> para vazio.</summary>
        public string? Limpo
        {
            get
            {
                if (texto is null)
                {
                    return null;
                }

                var limpo = Espacos().Replace(texto.Replace(' ', ' '), " ").Trim();
                return limpo.Length == 0 ? null : limpo;
            }
        }

        /// <summary>
        /// <c>dd/mm/aaaa</c> ou <c>dd/mm/aaaa HH:MM[:SS]</c> no fuso de Brasília. Ilegível vira <c>null</c>:
        /// data ruim degrada uma movimentação; exceção derrubaria a consulta inteira.
        /// </summary>
        public DateTimeOffset? ComoDataHoraBr
        {
            get
            {
                if (texto is null || DataBr().Match(texto) is not { Success: true } m)
                {
                    return null;
                }

                int Grupo(int i) => m.Groups[i].Success ? int.Parse(m.Groups[i].ValueSpan, CultureInfo.InvariantCulture) : 0;
                try
                {
                    var local = new DateTime(Grupo(3), Grupo(2), Grupo(1), Grupo(4), Grupo(5), Grupo(6), DateTimeKind.Unspecified);
                    return new DateTimeOffset(local, FusoBrasilia.GetUtcOffset(local));
                }
                catch (ArgumentOutOfRangeException)
                {
                    return null;
                }
            }
        }

        public string SemAcento
        {
            get
            {
                var decomposto = (texto ?? string.Empty).Normalize(NormalizationForm.FormD);
                var sb = new StringBuilder(decomposto.Length);
                foreach (var c in decomposto)
                {
                    if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                    {
                        sb.Append(char.ToLowerInvariant(c));
                    }
                }

                return sb.ToString();
            }
        }
    }

    /// <summary>
    /// Polo a partir do tipo de participação ("Reqte", "Apelado"…). "Autor do fato" (termo
    /// circunstanciado) é o acusado, por isso o passivo é testado antes.
    /// </summary>
    public static Polo InferirPolo(string tipo)
    {
        var normalizado = tipo.SemAcento.Trim(' ', ':');
        if (PoloPassivo().IsMatch(normalizado))
        {
            return Polo.Passivo;
        }

        return PoloAtivo().IsMatch(normalizado) ? Polo.Ativo : Polo.Outros;
    }

    /// <summary>Data/hora no formato ISO que o Python usava para o id (<c>2026-11-19T17:45:11-03:00</c>).</summary>
    public static string ComoIso(this DateTimeOffset valor) =>
        valor.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);

    [GeneratedRegex(@"\s+")]
    private static partial Regex Espacos();

    [GeneratedRegex(@"(\d{2})/(\d{2})/(\d{4})(?:\D+(\d{2}):(\d{2})(?::(\d{2}))?)?")]
    private static partial Regex DataBr();

    [GeneratedRegex(@"^(autora? do fato|reu|re|reus|res|reqd[oa]?s?|requerid[oa]s?|executad[oa]s?|exectd[oa]?s?|apelad[oa]s?|agravad[oa]s?|embargad[oa]s?|impetrad[oa]s?|reclamad[oa]s?|recorrid[oa]s?|querelad[oa]s?|denunciad[oa]s?|indiciad[oa]s?|averiguad[oa]s?|investigad[oa]s?|acusad[oa]s?|demandad[oa]s?|promovid[oa]s?)\b")]
    private static partial Regex PoloPassivo();

    [GeneratedRegex(@"^(autora?s?|autores|reqte|requerentes?|exeqte|exequentes?|apelantes?|agravantes?|embargantes?|impetrantes?|reclamantes?|recorrentes?|querelantes?|demandantes?|promoventes?|ministerio publico|justica publica)\b")]
    private static partial Regex PoloAtivo();
}
