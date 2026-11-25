"""Scraper da nova Consulta Pública do PJe (front-end Angular + API REST).

Os tribunais que migraram para a nova consulta pública expõem uma API JSON sem captcha,
consumida pelo próprio front-end (descoberta lendo os bundles do Angular em ``assets/env.js``
e ``chunk-*.js``)::

    GET {api}/v1/processos?numeroProcesso=…&page=0      → lista (idProcesso opaco)
    GET {api}/v1/processos/{id}/dados                    → metadados
    GET {api}/v1/processos/{id}/poloAtivo?page=N         → partes (10 por página)
    GET {api}/v1/processos/{id}/poloPassivo?page=N
    GET {api}/v1/processos/{id}/outrosInteressados?page=N
    GET {api}/v1/processos/{id}/movimentacoes?page=N     → 15 por página, mais recente primeiro

Envelope: ``{"status","code","messages","result","pageInfo":{"current","last","size","count"}}``.
``page`` é 0-based, ``pageInfo.current`` é 1-based. Não encontrado = ``result: []``.

O ``idProcesso`` é um token cifrado que muda a cada busca — não serve como chave estável;
a chave é sempre o número CNJ.

A antiga consulta JSF (``ConsultaPublica/listView.seam``) dos tribunais que ainda não
migraram costuma exigir captcha e não é suportada.
"""

from __future__ import annotations

import re
from datetime import datetime
from collections.abc import Callable
from typing import TYPE_CHECKING, Any

from juscraper.errors import RespostaInesperada
from juscraper.models import Advogado, Movimentacao, Parte, Polo, Processo
from juscraper.scrapers.base import (
    FUSO_BRASILIA,
    OpcoesConsulta,
    ResultadoScraper,
    Scraper,
    data_hora_br,
    limpar,
)

if TYPE_CHECKING:
    from juscraper.cnj import NumeroCnj
    from juscraper.http import Sessao

SISTEMA = "PJe"

_CODIGO_TABELA = re.compile(r"\s*\(\d+\)\s*$")
_SEPARADOR_ASSUNTO = re.compile(r"\s\(\d+\)(?:\s-\s|$)")
_OAB = re.compile(r"OAB\s+([A-Z]{2}\s?\d+[A-Z]?)")
_TIPOS_ADVOGADO = {"ADVOGADO", "ADVOGADA", "PROCURADOR", "DEFENSOR PÚBLICO", "DEFENSOR"}
_MAX_PAGINAS_PARTES = 10
_MAX_PROCESSOS_POR_NUMERO = 5


class PjeConsultaPublicaScraper(Scraper):
    sistema = SISTEMA

    def __init__(self, tribunal: str, api_url: str, *, grau: str = "G1", url_portal: str | None = None) -> None:
        super().__init__(tribunal)
        self.api_url = api_url.rstrip("/")
        self.grau = grau
        self.url_portal = url_portal

    async def consultar(
        self, numero: NumeroCnj, sessao: Sessao, opcoes: OpcoesConsulta
    ) -> ResultadoScraper:
        busca = await self._get(sessao, "/v1/processos", {"numeroProcesso": numero.formatado, "page": 0})
        itens = busca.get("result") or []
        avisos = [m for m in busca.get("messages") or [] if isinstance(m, str)]

        processos = []
        for item in itens[:_MAX_PROCESSOS_POR_NUMERO]:
            processos.append(await self._detalhar(item, numero, sessao, opcoes))
        return ResultadoScraper(processos, avisos)

    async def _detalhar(
        self, item: dict[str, Any], numero: NumeroCnj, sessao: Sessao, opcoes: OpcoesConsulta
    ) -> Processo:
        id_processo = item.get("idProcesso")
        if not id_processo:
            raise RespostaInesperada("PJe: item de busca sem idProcesso.", tribunal=self.tribunal)
        base = f"/v1/processos/{id_processo}"

        dados = (await self._get(sessao, f"{base}/dados")).get("result") or {}

        partes: list[Parte] = []
        for recurso, polo in (
            ("poloAtivo", Polo.ATIVO),
            ("poloPassivo", Polo.PASSIVO),
            ("outrosInteressados", Polo.OUTROS),
        ):
            registros_partes, _ = await self._paginar(sessao, f"{base}/{recurso}", _MAX_PAGINAS_PARTES)
            partes.extend(parse_partes(registros_partes, polo))

        limite = opcoes.max_paginas_movimentacoes
        if opcoes.max_movimentacoes is not None:
            limite = min(limite, -(-opcoes.max_movimentacoes // 15))
        registros, completos = await self._paginar(
            sessao, f"{base}/movimentacoes", limite, parar=_parar_antes_de(opcoes)
        )
        movimentos = parse_movimentacoes(registros)
        if opcoes.max_movimentacoes is not None and len(movimentos) > opcoes.max_movimentacoes:
            movimentos, completos = movimentos[: opcoes.max_movimentacoes], False
        for movimento in movimentos:
            movimento.instancia = self.grau

        processo = parse_dados(dados, item, tribunal=self.tribunal, grau=self.grau)
        processo.numero_processo = processo.numero_processo or numero.formatado
        processo.partes = partes
        processo.movimentos = movimentos
        processo.movimentos_completos = completos
        processo.url_consulta = self.url_portal
        return processo

    async def _get(self, sessao: Sessao, caminho: str, params: dict[str, Any] | None = None) -> dict[str, Any]:
        corpo = await sessao.get_json(
            f"{self.api_url}{caminho}", params=params, headers={"Accept": "application/json"}
        )
        if not isinstance(corpo, dict) or corpo.get("status") not in (None, "ok"):
            raise RespostaInesperada(f"PJe respondeu fora do envelope esperado em {caminho}.", tribunal=self.tribunal)
        return corpo

    async def _paginar(
        self,
        sessao: Sessao,
        caminho: str,
        max_paginas: int,
        parar: Callable[[dict[str, Any]], bool] | None = None,
    ) -> tuple[list[dict[str, Any]], bool]:
        registros: list[dict[str, Any]] = []
        pagina = 0
        while pagina < max_paginas:
            corpo = await self._get(sessao, caminho, {"page": pagina})
            novos = [r for r in corpo.get("result") or [] if isinstance(r, dict)]
            registros.extend(novos)
            if parar and novos and parar(novos[-1]):
                return registros, True
            ultima = int((corpo.get("pageInfo") or {}).get("last") or 0)
            pagina += 1
            if pagina >= ultima:
                return registros, True
        return registros, False


def _parar_antes_de(opcoes: OpcoesConsulta) -> Callable[[dict[str, Any]], bool] | None:
    if opcoes.desde is None:
        return None

    def parar(registro: dict[str, Any]) -> bool:
        data = data_hora_br(registro.get("dataAtualizacao"))
        return data is not None and data.date() < opcoes.desde  # type: ignore[operator]

    return parar


# -- parsers (puros) ------------------------------------------------------------------


def parse_dados(dados: dict[str, Any], item: dict[str, Any], *, tribunal: str, grau: str) -> Processo:
    assuntos = _assuntos(dados.get("assunto")) or ([a] if (a := limpar(item.get("assunto"))) else [])
    distribuicao = _iso(dados.get("dataDistribuicao"))
    return Processo(
        numero_processo=limpar(dados.get("numeroProcesso")) or limpar(item.get("numeroProcesso")) or "",
        tribunal=tribunal,
        grau=grau,
        sistema=SISTEMA,
        data_ajuizamento=distribuicao.date() if distribuicao else None,
        classe=_sem_codigo(dados.get("classeJudicial")) or limpar(item.get("classe")),
        orgao_julgador=limpar(dados.get("orgaoJulgadorColegiado")) or limpar(dados.get("orgaoJulgador")),
        assuntos=assuntos,
        foro=limpar(dados.get("jurisdicao")),
        em_segredo_de_justica=_sigiloso(item),
    )


def parse_partes(registros: list[dict[str, Any]], polo: Polo) -> list[Parte]:
    """O PJe lista advogados como participantes do mesmo polo, logo após a parte que
    representam; aqui eles são agregados à parte anterior.

    O campo ``participante`` traz CPF/CNPJ por extenso. Ele é deliberadamente descartado:
    o Iustitia não precisa do documento para acompanhar movimentações (minimização, LGPD).
    """
    partes: list[Parte] = []
    for registro in registros:
        nome = limpar(registro.get("nome"))
        tipo = limpar(registro.get("tipo")) or ""
        if not nome:
            continue
        if tipo.upper() in _TIPOS_ADVOGADO:
            oab = _OAB.search(registro.get("participante") or "")
            advogado = Advogado(nome=nome, oab=oab.group(1).replace(" ", "") if oab else None)
            if partes:
                partes[-1].advogados.append(advogado)
            continue
        partes.append(Parte(nome=nome, tipo=tipo.title(), polo=polo))
    return partes


def parse_movimentacoes(registros: list[dict[str, Any]]) -> list[Movimentacao]:
    movimentos = []
    for registro in registros:
        # Movimento excluído pelo cartório: o PJe mantém a linha riscada na tela, mas ela
        # não é andamento válido e não deve disparar alerta no Iustitia.
        if registro.get("movimentoExcluido"):
            continue
        nome = limpar(registro.get("movimento"))
        if not nome:
            continue
        movimentos.append(
            Movimentacao(
                nome=nome,
                data_hora=data_hora_br(registro.get("dataAtualizacao")),
                complemento=limpar(registro.get("documento")),
                possui_documento=bool(registro.get("idDocumento")),
            )
        )
    return movimentos


def _assuntos(texto: str | None) -> list[str]:
    """``"DIREITO CIVIL (899) - Obrigações (7681) - Espécies de Títulos (7717)"`` é o
    caminho na árvore da Tabela Unificada; o assunto é a folha."""
    texto = limpar(texto)
    if not texto:
        return []
    niveis = [n.strip() for n in _SEPARADOR_ASSUNTO.split(texto) if n and n.strip()]
    return [niveis[-1]] if niveis else []


def _sem_codigo(texto: str | None) -> str | None:
    texto = limpar(texto)
    return _CODIGO_TABELA.sub("", texto) if texto else None


def _iso(texto: str | None) -> datetime | None:
    if not texto:
        return None
    try:
        valor = datetime.fromisoformat(texto)
    except ValueError:
        return None
    return valor if valor.tzinfo else valor.replace(tzinfo=FUSO_BRASILIA)


def _sigiloso(item: dict[str, Any]) -> bool:
    polos = (item.get("partes") or {}).values()
    return any(isinstance(p, dict) and p.get("sigiloso") for p in polos)
