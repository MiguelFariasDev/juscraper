"""Modelo de dados devolvido pelos scrapers.

Os nomes dos campos (em camelCase no JSON) seguem o ``ProcessoTribunal`` e o
``MovimentoProcesso`` do Iustitia (Legal.Application/Abstractions/IConsultaProcessoTribunal),
para que o adaptador .NET seja uma desserialização direta. Campos extras (partes,
complemento da movimentação, valor da causa…) ficam disponíveis sem quebrar o contrato.
"""

from __future__ import annotations

from datetime import date, datetime, timezone
from enum import StrEnum

from pydantic import BaseModel, ConfigDict, Field
from pydantic.alias_generators import to_camel


class _Modelo(BaseModel):
    model_config = ConfigDict(alias_generator=to_camel, populate_by_name=True)


class Polo(StrEnum):
    ATIVO = "ATIVO"
    PASSIVO = "PASSIVO"
    OUTROS = "OUTROS"


class Movimentacao(_Modelo):
    """Um andamento do processo.

    ``nome`` é o título do movimento (ex.: "Conclusos para Despacho"); ``complemento`` é o
    texto livre que o cartório anexa a ele (no e-SAJ, a linha em itálico).
    """

    nome: str
    data_hora: datetime | None = Field(
        default=None,
        description="Quando ocorreu. No e-SAJ só há a data; a hora fica 00:00 no fuso de Brasília.",
    )
    complemento: str | None = None
    possui_documento: bool = False
    instancia: str | None = Field(default=None, description="Grau de onde veio: G1, G2…")


class Advogado(_Modelo):
    nome: str
    oab: str | None = None


class Parte(_Modelo):
    nome: str
    tipo: str = Field(description="Tipo de participação como o tribunal escreve: Autor, Réu, Apelante…")
    polo: Polo | None = None
    advogados: list[Advogado] = Field(default_factory=list)


class Processo(_Modelo):
    """Um processo em UMA instância. O mesmo número pode existir no 1º e no 2º grau."""

    numero_processo: str = Field(description="Número mascarado, forma canônica.")
    tribunal: str
    grau: str | None = Field(default=None, description="G1, G2, JE, TR…")
    sistema: str = Field(description="Sistema de origem: e-SAJ, PJe…")
    data_ajuizamento: date | None = None
    em_segredo_de_justica: bool = False
    classe: str | None = None
    orgao_julgador: str | None = None
    assuntos: list[str] = Field(default_factory=list)
    movimentos: list[Movimentacao] = Field(
        default_factory=list, description="Do mais recente para o mais antigo."
    )
    codigo_municipio_ibge: int | None = None

    # -- além do contrato do Iustitia --
    situacao: str | None = None
    area: str | None = None
    foro: str | None = None
    juiz: str | None = Field(default=None, description="Juiz (1º grau) ou relator (2º grau).")
    valor_da_causa: str | None = None
    partes: list[Parte] = Field(default_factory=list)
    url_consulta: str | None = None
    movimentos_completos: bool = Field(
        default=True,
        description="Falso quando a lista foi truncada por limite de páginas/movimentações.",
    )


class ResultadoConsulta(_Modelo):
    """Resposta de uma consulta por número. ``encontrado=False`` é desfecho normal, não erro."""

    numero_processo: str
    tribunal: str | None
    encontrado: bool
    processos: list[Processo] = Field(default_factory=list)
    consultado_em: datetime
    avisos: list[str] = Field(
        default_factory=list,
        description="Problemas não fatais: uma instância fora do ar enquanto a outra respondeu, etc.",
    )

    @property
    def movimentos_consolidados(self) -> list[Movimentacao]:
        """Movimentos de todas as instâncias, do mais recente para o mais antigo."""
        todos = [m for p in self.processos for m in p.movimentos]
        return sorted(todos, key=lambda m: m.data_hora or _SEM_DATA, reverse=True)


_SEM_DATA = datetime.min.replace(tzinfo=timezone.utc)
