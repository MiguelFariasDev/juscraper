using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;

namespace Juscraper.Scrapers.Esaj;

public enum TipoPaginaEsaj
{
    NaoEncontrado,
    Segredo,
    Detalhe,
    Lista,
}

/// <param name="CodigoAjax">Código interno do e-SAJ quando as movimentações vêm sob demanda (AJAX).</param>
public sealed record PaginaDetalhe(Processo Processo, string? CodigoAjax);

/// <summary>
/// Parsers puros (HTML → dados) do e-SAJ, testados com cassetes sem rede.
///
/// As movimentações vêm em dois formatos, conforme a versão do e-SAJ do tribunal:
/// <list type="bullet">
/// <item><b>inline</b> (ex.: TJAL): <c>tbody#tabelaTodasMovimentacoes</c>;</item>
/// <item><b>sob demanda</b> (TJSP, e-SAJ 26.x): <c>carregarMovimentacoesAjax.do</c>, 5 por vez, com
/// cursor cifrado (keyset). 1º grau: <c>processo.codigo</c> e cursor em
/// <c>input#cursorMovimentacoesPaginado</c>/<c>tr#trCursorMovimentacoes</c>; 2º grau:
/// <c>cdProcesso</c> e cursor em <c>a#linkMaisRecolherMovimentacoes</c>/<c>tr#metaPaginaMovimentacoes</c>.</item>
/// </list>
/// </summary>
public static partial class EsajParser
{
    public const string Sistema = "e-SAJ";

    private static readonly HtmlParser Parser = new();

    public static IHtmlDocument Ler(string html) => Parser.ParseDocument(html);

    /// <summary>Fragmento AJAX: linhas &lt;tr&gt; soltas, que só sobrevivem dentro de uma tabela.</summary>
    public static IHtmlDocument LerFragmento(string html) => Parser.ParseDocument($"<table><tbody>{html}</tbody></table>");

    public static TipoPaginaEsaj Classificar(string html)
    {
        var documento = Ler(html);
        var mensagem = documento.GetElementById("mensagemRetorno");
        if (mensagem is not null && NaoEncontrado().IsMatch(mensagem.TextContent))
        {
            return TipoPaginaEsaj.NaoEncontrado;
        }

        if (PopupSenhaAtivo().IsMatch(html))
        {
            return TipoPaginaEsaj.Segredo;
        }

        if (documento.GetElementById("numeroProcesso")?.TextContent.Limpo is not null)
        {
            return TipoPaginaEsaj.Detalhe;
        }

        if (ParseLista(html).Count > 0)
        {
            return TipoPaginaEsaj.Lista;
        }

        throw new RespostaInesperadaException(mensagem is not null
            ? $"e-SAJ respondeu: {mensagem.TextContent.Limpo}"
            : "Página do e-SAJ sem dados de processo, lista ou mensagem reconhecível.");
    }

    /// <summary>Códigos internos dos processos de uma página de lista, na ordem e sem repetição.</summary>
    public static IReadOnlyList<string> ParseLista(string html)
    {
        var codigos = LinkShow().Matches(html).Select(m => m.Groups[1].Value).ToList();
        codigos.AddRange(Ler(html).QuerySelectorAll("input[name='processoSelecionado']")
            .Select(i => i.GetAttribute("value") ?? string.Empty));
        return codigos.Where(c => c.Length > 0).Distinct(StringComparer.Ordinal).ToList();
    }

    public static PaginaDetalhe ParseDetalhe(string html, string tribunal, string grau)
    {
        var documento = Ler(html);
        var numero = Texto(documento, "numeroProcesso");

        if (PopupSenhaAtivo().IsMatch(html))
        {
            return new PaginaDetalhe(
                new Processo { NumeroProcesso = numero ?? string.Empty, Tribunal = tribunal, Grau = grau, Sistema = Sistema, EmSegredoDeJustica = true },
                null);
        }

        if (numero is null)
        {
            throw new RespostaInesperadaException("Página de detalhe do e-SAJ sem #numeroProcesso — layout mudou?", tribunal);
        }

        // As etiquetas de situação são irmãs do número; o contêiner muda entre versões do e-SAJ.
        var situacoes = documento.GetElementById("numeroProcesso")?.ParentElement?.QuerySelectorAll(".unj-tag")
            .Select(t => t.TextContent.Limpo).OfType<string>().ToList() ?? [];
        var assunto = Texto(documento, "assuntoProcesso");

        var processo = new Processo
        {
            NumeroProcesso = numero,
            Tribunal = tribunal,
            Grau = grau,
            Sistema = Sistema,
            DataAjuizamento = Texto(documento, "dataHoraDistribuicaoProcesso").ComoDataHoraBr is { } distribuicao
                ? DateOnly.FromDateTime(distribuicao.DateTime)
                : null,
            Classe = Texto(documento, "classeProcesso"),
            OrgaoJulgador = Texto(documento, "orgaoJulgadorProcesso") ?? Texto(documento, "varaProcesso"),
            Assuntos = assunto is null ? [] : [assunto],
            Situacao = situacoes.Count > 0 ? string.Join(", ", situacoes) : Texto(documento, "situacaoProcesso"),
            Area = Texto(documento, "areaProcesso"),
            Foro = Texto(documento, "foroProcesso") ?? Rotulado(documento, "Origem"),
            Juiz = Texto(documento, "juizProcesso") ?? Texto(documento, "relatorProcesso"),
            ValorDaCausa = Texto(documento, "valorAcaoProcesso"),
            Partes = ParsePartes(documento),
        };

        string? codigoAjax = null;
        if (documento.GetElementById("btnExibirMovimentacoes") is not null && CodigoCpopg().Match(html) is { Success: true } cpopg)
        {
            codigoAjax = cpopg.Groups[1].Value;
        }
        else if (documento.GetElementById("linkExibirMovimentacoes") is not null && CodigoCposg().Match(html) is { Success: true } cposg)
        {
            codigoAjax = cposg.Groups[1].Value;
        }
        else if ((documento.GetElementById("tabelaTodasMovimentacoes") ?? documento.GetElementById("tabelaUltimasMovimentacoes")) is { } corpo)
        {
            processo.Movimentos = ParseLinhasMovimentacao(corpo);
        }

        return new PaginaDetalhe(processo, codigoAjax);
    }

    public static List<Parte> ParsePartes(IDocument documento)
    {
        // "Todas as partes" é a lista completa quando existe; a de "principais" duplicaria nomes.
        var tabela = documento.GetElementById("tableTodasPartes") ?? documento.GetElementById("tablePartesPrincipais");
        if (tabela is null)
        {
            return [];
        }

        var partes = new List<Parte>();
        foreach (var linha in tabela.QuerySelectorAll("tr"))
        {
            var rotulo = linha.QuerySelector("td.label");
            var celula = linha.QuerySelector("td.nomeParteEAdvogado");
            if (rotulo is null || celula is null)
            {
                continue;
            }

            var tipo = (rotulo.TextContent.Limpo ?? string.Empty).TrimEnd(':').Trim();
            var (nome, advogados) = NomeEAdvogados(celula);
            if (nome is null)
            {
                continue;
            }

            var polo = linha.ClassList.Contains("poloAtivo") ? Polo.Ativo
                : linha.ClassList.Contains("poloPassivo") ? Polo.Passivo
                : Normalizacao.InferirPolo(tipo);
            partes.Add(new Parte { Nome = nome, Tipo = tipo, Polo = polo, Advogados = advogados });
        }

        return partes;
    }

    /// <summary>
    /// A célula mistura nome da parte, rótulos ("Advogado:") e nomes de advogados como nós
    /// irmãos — não há um elemento por advogado. Uma pequena máquina de estados resolve.
    /// </summary>
    private static (string? Nome, List<Advogado> Advogados) NomeEAdvogados(IElement celula)
    {
        var nome = new List<string>();
        var advogados = new List<Advogado>();
        var buffer = new List<string>();
        var emAdvogado = false;

        void Descarregar()
        {
            var texto = string.Join(' ', buffer).Limpo;
            buffer.Clear();
            if (texto is null)
            {
                return;
            }

            if (emAdvogado)
            {
                advogados.Add(new Advogado(texto));
            }
            else
            {
                nome.Add(texto);
            }
        }

        foreach (var no in celula.ChildNodes)
        {
            switch (no)
            {
                case IText texto:
                    buffer.Add(texto.Data);
                    break;
                case IElement { LocalName: "span" } span when span.ClassList.Contains("mensagemExibindo"):
                    Descarregar();
                    emAdvogado = true;
                    break;
                case IElement { LocalName: "br" }:
                    Descarregar();
                    break;
                case IElement { LocalName: "input" } input:
                    Descarregar();
                    if (advogados.Count > 0 && input.GetAttribute("value").Limpo is { } oab)
                    {
                        advogados[^1] = advogados[^1] with { Oab = oab };
                    }

                    break;
                case IElement outro:
                    buffer.Add(outro.TextContent);
                    break;
            }
        }

        Descarregar();
        return (string.Join(' ', nome).Limpo, advogados);
    }

    public static List<Movimentacao> ParseLinhasMovimentacao(IParentNode raiz)
    {
        var movimentos = new List<Movimentacao>();
        foreach (var linha in raiz.QuerySelectorAll("tr.containerMovimentacao, tr.movimentacaoProcesso"))
        {
            var data = linha.QuerySelector("td[class^='dataMovimentacao']");
            var descricao = linha.QuerySelector("td[class^='descricaoMovimentacao']");
            if (descricao is null)
            {
                continue;
            }

            var italico = descricao.QuerySelector("span[style*='italic']");
            var complemento = italico?.TextContent.Limpo;
            italico?.Remove();
            if (descricao.TextContent.Limpo is not { } nome)
            {
                continue;
            }

            movimentos.Add(new Movimentacao
            {
                Nome = nome,
                DataHora = data?.TextContent.ComoDataHoraBr,
                Complemento = complemento,
                PossuiDocumento = linha.QuerySelector("a.linkMovVincProc") is not null,
            });
        }

        return movimentos;
    }

    /// <summary>Cursor da próxima página de movimentações, ou null se esta foi a última.</summary>
    public static string? ProximoCursor(IDocument documento)
    {
        if (documento.GetElementById("metaPaginaMovimentacoes") is { } meta)
        {
            return meta.GetAttribute("data-tem-mais-paginas") == "true" ? meta.GetAttribute("data-cursor") : null;
        }

        return (documento.GetElementById("trCursorMovimentacoes")?.GetAttribute("data-cursor")
                ?? documento.GetElementById("cursorMovimentacoesPaginado")?.GetAttribute("value")
                ?? documento.GetElementById("linkMaisRecolherMovimentacoes")?.GetAttribute("data-cursor")).Limpo;
    }

    private static string? Texto(IDocument documento, string id) => documento.GetElementById(id)?.TextContent.Limpo;

    /// <summary>Valor de um campo sem id, localizado pelo rótulo <c>span.unj-label</c>.</summary>
    private static string? Rotulado(IDocument documento, string rotulo) =>
        documento.QuerySelectorAll("span.unj-label")
            .FirstOrDefault(l => l.TextContent.Limpo == rotulo)?.NextElementSibling?.TextContent.Limpo;

    [GeneratedRegex(@"N[ãa]o existem informa[çc][õo]es dispon[íi]veis", RegexOptions.IgnoreCase)]
    private static partial Regex NaoEncontrado();

    [GeneratedRegex(@"popupSenha\s*:\s*\{\s*mostrar\s*:\s*'true'\s*==\s*'true'")]
    private static partial Regex PopupSenhaAtivo();

    [GeneratedRegex(@"\$\.saj\.movimentacoes\s*=\s*\{\s*codigoProcesso\s*:\s*'([^']+)'")]
    private static partial Regex CodigoCpopg();

    [GeneratedRegex(@"var\s+cdProcessoMovimentacoes\s*=\s*'([^']+)'")]
    private static partial Regex CodigoCposg();

    [GeneratedRegex(@"show\.do\?processo\.codigo=([A-Z0-9]+)")]
    private static partial Regex LinkShow();
}
