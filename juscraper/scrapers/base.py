"""Contrato comum dos scrapers e utilitários de normalização."""

from __future__ import annotations

import abc
import re
import unicodedata
from dataclasses import dataclass
from datetime import date, datetime
from typing import TYPE_CHECKING
from zoneinfo import ZoneInfo

from juscraper.models import Polo, Processo

if TYPE_CHECKING:
    from juscraper.cnj import NumeroCnj
    from juscraper.http import Sessao

FUSO_BRASILIA = ZoneInfo("America/Sao_Paulo")

_ESPACOS = re.compile(r"\s+")
_DATA_BR = re.compile(r"(\d{2})/(\d{2})/(\d{4})(?:\D+(\d{2}):(\d{2})(?::(\d{2}))?)?")


@dataclass(frozen=True, slots=True)
class OpcoesConsulta:
    #: Instâncias a consultar, quando o sistema separa (e-SAJ: "1" = cpopg, "2" = cposg).
    instancias: tuple[str, ...] = ("1", "2")
    #: Teto de páginas de movimentações por processo (cada página é uma requisição).
    max_paginas_movimentacoes: int = 100
    #: Para de paginar ao atingir este número de movimentações (None = todas).
    max_movimentacoes: int | None = None


@dataclass(slots=True)
class ResultadoScraper:
    processos: list[Processo]
    avisos: list[str]


class Scraper(abc.ABC):
    sistema: str

    def __init__(self, tribunal: str) -> None:
        self.tribunal = tribunal

    @abc.abstractmethod
    async def consultar(
        self, numero: NumeroCnj, sessao: Sessao, opcoes: OpcoesConsulta
    ) -> ResultadoScraper:
        """Consulta o número. Lista vazia = não encontrado (desfecho normal, não erro)."""


# -- normalização ---------------------------------------------------------------------


def limpar(texto: str | None) -> str | None:
    """Colapsa espaços (incluindo &nbsp;) e devolve None para string vazia."""
    if texto is None:
        return None
    texto = _ESPACOS.sub(" ", texto.replace("\xa0", " ")).strip()
    return texto or None


def data_hora_br(texto: str | None) -> datetime | None:
    """``dd/mm/aaaa`` ou ``dd/mm/aaaa HH:MM[:SS]`` no fuso de Brasília. Ilegível vira None.

    Data ilegível degrada uma movimentação; exceção derrubaria a consulta inteira.
    """
    if not texto or not (m := _DATA_BR.search(texto)):
        return None
    dia, mes, ano, hora, minuto, segundo = m.groups()
    try:
        return datetime(
            int(ano), int(mes), int(dia),
            int(hora or 0), int(minuto or 0), int(segundo or 0),
            tzinfo=FUSO_BRASILIA,
        )
    except ValueError:
        return None


def data_br(texto: str | None) -> date | None:
    dh = data_hora_br(texto)
    return dh.date() if dh else None


_POLO_PASSIVO = re.compile(
    r"^(autora? do fato|reu|re|reus|res|reqd[oa]?s?|requerid[oa]s?|executad[oa]s?|exectd[oa]?s?|"
    r"apelad[oa]s?|agravad[oa]s?|embargad[oa]s?|impetrad[oa]s?|reclamad[oa]s?|recorrid[oa]s?|"
    r"querelad[oa]s?|denunciad[oa]s?|indiciad[oa]s?|averiguad[oa]s?|investigad[oa]s?|"
    r"acusad[oa]s?|demandad[oa]s?|promovid[oa]s?)\b"
)
_POLO_ATIVO = re.compile(
    r"^(autora?s?|autores|reqte|requerentes?|exeqte|exequentes?|apelantes?|agravantes?|"
    r"embargantes?|impetrantes?|reclamantes?|recorrentes?|querelantes?|demandantes?|"
    r"promoventes?|ministerio publico|justica publica)\b"
)


def _sem_acento(texto: str) -> str:
    return unicodedata.normalize("NFKD", texto).encode("ascii", "ignore").decode().lower()


def inferir_polo(tipo: str) -> Polo:
    """Polo a partir do tipo de participação ("Reqte", "Apelado"…).

    "Autor do fato" (termo circunstanciado) é o acusado, por isso o passivo é testado antes.
    """
    tipo = _sem_acento(tipo).strip(" :")
    if _POLO_PASSIVO.match(tipo):
        return Polo.PASSIVO
    if _POLO_ATIVO.match(tipo):
        return Polo.ATIVO
    return Polo.OUTROS
