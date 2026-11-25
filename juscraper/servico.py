"""Ponto de entrada de alto nível: ``Juscraper.consultar("0000248-42.2024.8.26.0319")``."""

from __future__ import annotations

import time
from collections import OrderedDict
from datetime import datetime, timezone

import httpx

from juscraper import tribunais
from juscraper.cnj import NumeroCnj
from juscraper.http import ConfigHttp, Limitadores, Sessao
from juscraper.models import ResultadoConsulta
from juscraper.scrapers.base import OpcoesConsulta


class _CacheTtl:
    """Cache em memória com TTL e teto de entradas (LRU).

    Inclui o "não encontrado": é o resultado mais reconsultado, porque quem digita acha que
    errou o número (mesma observação do ADR-064 do Iustitia). Erros NÃO entram no cache.
    """

    def __init__(self, ttl: float, maximo: int = 2000) -> None:
        self._ttl = ttl
        self._maximo = maximo
        self._itens: OrderedDict[tuple, tuple[float, ResultadoConsulta]] = OrderedDict()

    def get(self, chave: tuple) -> ResultadoConsulta | None:
        item = self._itens.get(chave)
        if item is None:
            return None
        expira, valor = item
        if expira < time.monotonic():
            del self._itens[chave]
            return None
        self._itens.move_to_end(chave)
        return valor

    def set(self, chave: tuple, valor: ResultadoConsulta) -> None:
        if self._ttl <= 0:
            return
        self._itens[chave] = (time.monotonic() + self._ttl, valor)
        self._itens.move_to_end(chave)
        while len(self._itens) > self._maximo:
            self._itens.popitem(last=False)


class Juscraper:
    def __init__(
        self,
        config: ConfigHttp | None = None,
        *,
        cache_ttl: float = 1800,
        transport: httpx.AsyncBaseTransport | None = None,
    ) -> None:
        self.config = config or ConfigHttp()
        self._limitadores = Limitadores(self.config)
        self._cache = _CacheTtl(cache_ttl)
        self._transport = transport

    async def consultar(
        self,
        numero: str,
        *,
        instancias: tuple[str, ...] = ("1", "2"),
        max_movimentacoes: int | None = None,
        max_paginas_movimentacoes: int = 100,
        usar_cache: bool = True,
    ) -> ResultadoConsulta:
        """Consulta o processo no tribunal derivado do próprio número.

        O tribunal não é parâmetro: está codificado no número (campo J.TR). Aceitá-lo de
        fora abriria espaço para divergir do número e devolver "não encontrado" sem causa.
        """
        cnj = NumeroCnj.parse(numero)
        tribunal = tribunais.obter(cnj.tribunal)
        scraper = tribunal.scraper()

        opcoes = OpcoesConsulta(
            instancias=instancias,
            max_paginas_movimentacoes=max_paginas_movimentacoes,
            max_movimentacoes=max_movimentacoes,
        )
        chave = (cnj.digitos, opcoes)
        if usar_cache and (em_cache := self._cache.get(chave)):
            return em_cache

        async with Sessao(self.config, self._limitadores, tribunal=tribunal.sigla, transport=self._transport) as sessao:
            resultado = await scraper.consultar(cnj, sessao, opcoes)

        for processo in resultado.processos:
            processo.tribunal = tribunal.sigla
            processo.movimentos.sort(key=lambda m: m.data_hora is None)  # sem data vai pro fim, estável

        consulta = ResultadoConsulta(
            numero_processo=cnj.formatado,
            tribunal=tribunal.sigla,
            encontrado=bool(resultado.processos),
            processos=resultado.processos,
            consultado_em=datetime.now(timezone.utc),
            avisos=resultado.avisos,
        )
        # Resultado parcial (uma instância falhou) não entra no cache: congelaria por todo o
        # TTL uma falha que provavelmente é transitória.
        if not consulta.avisos:
            self._cache.set(chave, consulta)
        return consulta
