using System.Text.Json.Serialization;

namespace Juscraper;

// Os nomes (camelCase no JSON) seguem ProcessoTribunal/MovimentoProcesso do Iustitia
// (Legal.Application/Abstractions/IConsultaProcessoTribunal), para o adaptador .NET de lá ser
// uma desserialização direta. Campos extras não quebram o contrato.

[JsonConverter(typeof(JsonStringEnumConverter<Polo>))]
public enum Polo
{
    [JsonStringEnumMemberName("ATIVO")]
    Ativo,

    [JsonStringEnumMemberName("PASSIVO")]
    Passivo,

    [JsonStringEnumMemberName("OUTROS")]
    Outros,
}

/// <summary>Um andamento. <see cref="Nome"/> é o título; <see cref="Complemento"/>, o texto que o cartório anexa.</summary>
public sealed class Movimentacao
{
    /// <summary>Identificador estável entre consultas, para deduplicação (ver <see cref="Servicos.IdsMovimentacao"/>).</summary>
    public string? Id { get; set; }

    public required string Nome { get; init; }

    /// <summary>Quando ocorreu. No e-SAJ só há a data: a hora fica 00:00 no fuso de Brasília.</summary>
    public DateTimeOffset? DataHora { get; init; }

    public string? Complemento { get; init; }

    public bool PossuiDocumento { get; init; }

    /// <summary>Grau de onde veio: G1, G2…</summary>
    public string? Instancia { get; set; }
}

public sealed record Advogado(string Nome, string? Oab = null);

public sealed class Parte
{
    public required string Nome { get; init; }

    /// <summary>Tipo de participação como o tribunal escreve: Autor, Réu, Apelante…</summary>
    public required string Tipo { get; init; }

    public Polo? Polo { get; init; }

    public List<Advogado> Advogados { get; init; } = [];
}

/// <summary>Um processo em UMA instância. O mesmo número pode existir no 1º e no 2º grau.</summary>
public sealed class Processo
{
    public required string NumeroProcesso { get; set; }

    public required string Tribunal { get; set; }

    public string? Grau { get; init; }

    public required string Sistema { get; init; }

    public DateOnly? DataAjuizamento { get; init; }

    public bool EmSegredoDeJustica { get; init; }

    public string? Classe { get; init; }

    public string? OrgaoJulgador { get; init; }

    public List<string> Assuntos { get; init; } = [];

    /// <summary>Do mais recente para o mais antigo.</summary>
    public List<Movimentacao> Movimentos { get; set; } = [];

    public int? CodigoMunicipioIbge { get; init; }

    // -- além do contrato do Iustitia --

    public string? Situacao { get; init; }

    public string? Area { get; init; }

    public string? Foro { get; init; }

    /// <summary>Juiz (1º grau) ou relator (2º grau).</summary>
    public string? Juiz { get; init; }

    public string? ValorDaCausa { get; init; }

    public List<Parte> Partes { get; set; } = [];

    public string? UrlConsulta { get; set; }

    /// <summary>Falso quando a lista foi truncada por limite de páginas ou de movimentações.</summary>
    public bool MovimentosCompletos { get; set; } = true;
}

/// <summary>Resposta de uma consulta por número. <see cref="Encontrado"/> falso é desfecho normal.</summary>
public sealed class ResultadoConsulta
{
    public required string NumeroProcesso { get; init; }

    public string? Tribunal { get; init; }

    public bool Encontrado { get; init; }

    public List<Processo> Processos { get; init; } = [];

    public DateTimeOffset ConsultadoEm { get; init; }

    /// <summary>Problemas não fatais: uma instância fora do ar enquanto a outra respondeu, etc.</summary>
    public List<string> Avisos { get; init; } = [];

    /// <summary>Movimentos de todas as instâncias, do mais recente para o mais antigo.</summary>
    [JsonIgnore]
    public IEnumerable<Movimentacao> MovimentosConsolidados =>
        Processos.SelectMany(p => p.Movimentos).OrderByDescending(m => m.DataHora ?? DateTimeOffset.MinValue);
}
