"""Ponto de entrada de alto nível: ``Juscraper.consultar("0000248-42.2024.8.26.0319")``."""

from __future__ import annotations

import hashlib
import time
from collections import OrderedDict
from collections import Counter
from datetime import date, datetime, timezone

import httpx

from juscraper import tribunais
from juscraper.cnj import NumeroCnj
from juscraper.http import ConfigHttp, Limitadores, Sessao
from juscraper.models import Movimentacao, ResultadoConsulta
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
        desde: date | None = None,
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
            desde=desde,
        )
        chave = (cnj.digitos, opcoes)
        if usar_cache and (em_cache := self._cache.get(chave)):
            return em_cache

        async with Sessao(self.config, self._limitadores, tribunal=tribunal.sigla, transport=self._transport) as sessao:
            resultado = await scraper.consultar(cnj, sessao, opcoes)

        for processo in resultado.processos:
            processo.tribunal = tribunal.sigla
            processo.movimentos.sort(key=lambda m: m.data_hora is None)  # sem data vai pro fim, estável
            atribuir_ids(cnj.digitos, processo.movimentos)
            if desde is not None:
                processo.movimentos = [m for m in processo.movimentos if not opcoes.antes_da_janela(m)]

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


def atribuir_ids(digitos: str, movimentos: list[Movimentacao]) -> None:
    """Dá a cada movimentação um id estável entre consultas.

    O id é o hash de (processo, instância, data/hora, nome, complemento) mais um ORDINAL:
    o e-SAJ só informa a data, e dois andamentos idênticos no mesmo dia ("Expedido
    Certidão", duas vezes) são legítimos — sem o ordinal, o segundo seria descartado como
    duplicata e nunca notificado.

    O ordinal é contado do mais ANTIGO para o mais recente dentro do mesmo grupo idêntico.
    Andamentos novos entram no topo da lista, então os existentes não mudam de número. A
    garantia vale quando o dia inteiro foi lido — o que acontece com ``desde`` (a paginação
    só para depois de passar do dia) e com ``movimentosCompletos = true``. Com
    ``max_movimentacoes``, o dia mais antigo da lista pode estar cortado.
    """
    vistos: Counter[tuple] = Counter()
    for movimento in reversed(movimentos):
        chave = (
            movimento.instancia or "",
            movimento.data_hora.isoformat() if movimento.data_hora else "",
            movimento.nome,
            movimento.complemento or "",
        )
        vistos[chave] += 1
        bruto = "\x1f".join((digitos, *chave, str(vistos[chave])))
        movimento.id = hashlib.sha256(bruto.encode("utf-8")).hexdigest()[:32]
