"""API HTTP do juscraper — o que o Iustitia (ou qualquer outro sistema) consome.

Endpoints::

    GET /health
    GET /v1/tribunais
    GET /v1/processos/{numero}                     consulta completa (todas as instâncias)
    GET /v1/processos/{numero}/movimentacoes       só os andamentos, consolidados e ordenados

Convenções alinhadas ao Iustitia:

- não encontrado é **200** com ``encontrado: false`` (ADR-064 do Iustitia);
- erros em **ProblemDetails** (RFC 7807, ADR-040), com ``codigo`` estável e
  ``transitorio`` dizendo se vale tentar de novo;
- autenticação por ``X-Api-Key`` quando ``JUSCRAPER_API_KEYS`` estiver definida.

Uso: ``uvicorn juscraper.api:app`` ou ``juscraper serve``.
"""

from __future__ import annotations

import hmac
import logging
from contextlib import asynccontextmanager
from datetime import date, datetime, timezone
from typing import Annotated

from fastapi import Depends, FastAPI, Header, Path, Query, Request
from fastapi.responses import JSONResponse
from pydantic import Field
from pydantic_settings import BaseSettings, SettingsConfigDict

from juscraper import __version__, tribunais
from juscraper.errors import JuscraperError
from juscraper.http import ConfigHttp, PoliticaHost, USER_AGENT_PADRAO
from juscraper.models import Movimentacao, ResultadoConsulta, _Modelo
from juscraper.servico import Juscraper

log = logging.getLogger("juscraper")


class Configuracao(BaseSettings):
    model_config = SettingsConfigDict(env_prefix="JUSCRAPER_", env_file=".env", extra="ignore")

    api_keys: str = Field(default="", description="Chaves aceitas em X-Api-Key, separadas por vírgula.")
    cache_ttl: float = 1800
    timeout: float = 30
    tentativas: int = 3
    intervalo_minimo: float = 1.0
    concorrencia_por_host: int = 2
    user_agent: str = USER_AGENT_PADRAO
    max_paginas_movimentacoes: int = 100

    @property
    def chaves(self) -> list[str]:
        return [c.strip() for c in self.api_keys.split(",") if c.strip()]


def criar_app(config: Configuracao | None = None, servico: Juscraper | None = None) -> FastAPI:
    config = config or Configuracao()
    servico = servico or Juscraper(
        ConfigHttp(
            timeout=config.timeout,
            tentativas=config.tentativas,
            user_agent=config.user_agent,
            politica_padrao=PoliticaHost(config.intervalo_minimo, config.concorrencia_por_host),
        ),
        cache_ttl=config.cache_ttl,
    )

    @asynccontextmanager
    async def ciclo(_: FastAPI):
        if not config.chaves:
            log.warning("JUSCRAPER_API_KEYS vazia: a API está aberta. Defina chaves em produção.")
        yield

    app = FastAPI(
        title="juscraper",
        version=__version__,
        summary="Consulta de processos e movimentações nos portais públicos dos tribunais.",
        lifespan=ciclo,
    )

    async def autenticar(x_api_key: Annotated[str | None, Header()] = None) -> None:
        if not config.chaves:
            return
        if not x_api_key or not any(hmac.compare_digest(x_api_key, c) for c in config.chaves):
            raise _NaoAutorizado()

    @app.exception_handler(JuscraperError)
    async def _erro(request: Request, exc: JuscraperError) -> JSONResponse:
        return _problema(request, exc.http_status, exc.codigo, exc.mensagem, exc.tribunal, exc.transitorio)

    @app.exception_handler(_NaoAutorizado)
    async def _nao_autorizado(request: Request, exc: _NaoAutorizado) -> JSONResponse:
        return _problema(request, 401, "NAO_AUTORIZADO", "X-Api-Key ausente ou inválida.", None, False)

    @app.get("/health", tags=["infra"])
    async def health() -> dict[str, str]:
        return {"status": "ok", "versao": __version__}

    @app.get("/v1/tribunais", tags=["tribunais"], dependencies=[Depends(autenticar)])
    async def listar_tribunais() -> list[TribunalInfo]:
        return [
            TribunalInfo(sigla=t.sigla, sistema=t.sistema, status=t.status, portal=t.portal, observacao=t.observacao)
            for t in tribunais.TRIBUNAIS.values()
        ]

    @app.get(
        "/v1/processos/{numero}",
        tags=["processos"],
        dependencies=[Depends(autenticar)],
        response_model_by_alias=True,
    )
    async def consultar_processo(
        numero: Annotated[str, Path(description="Número CNJ, com ou sem máscara.")],
        instancias: Annotated[str, Query(description="Instâncias, ex.: '1,2'. Só afeta o e-SAJ.")] = "1,2",
        max_movimentacoes: Annotated[int | None, Query(ge=1, le=5000)] = None,
        sem_cache: Annotated[bool, Query(description="Ignora o cache e consulta o tribunal.")] = False,
    ) -> ResultadoConsulta:
        return await servico.consultar(
            numero,
            instancias=_instancias(instancias),
            max_movimentacoes=max_movimentacoes,
            max_paginas_movimentacoes=config.max_paginas_movimentacoes,
            usar_cache=not sem_cache,
        )

    @app.get(
        "/v1/processos/{numero}/movimentacoes",
        tags=["processos"],
        dependencies=[Depends(autenticar)],
        response_model_by_alias=True,
    )
    async def listar_movimentacoes(
        numero: str,
        desde: Annotated[
            date | None, Query(description="Só movimentações nesta data ou depois (sincronização incremental).")
        ] = None,
        instancias: str = "1,2",
        sem_cache: bool = False,
    ) -> MovimentacoesResposta:
        resultado = await servico.consultar(
            numero,
            instancias=_instancias(instancias),
            max_paginas_movimentacoes=config.max_paginas_movimentacoes,
            desde=desde,
            usar_cache=not sem_cache,
        )
        movimentos = resultado.movimentos_consolidados
        return MovimentacoesResposta(
            numero_processo=resultado.numero_processo,
            tribunal=resultado.tribunal,
            encontrado=resultado.encontrado,
            completas=all(p.movimentos_completos for p in resultado.processos),
            movimentos=movimentos,
            consultado_em=resultado.consultado_em,
            avisos=resultado.avisos,
        )

    return app


class _NaoAutorizado(Exception):
    pass


class TribunalInfo(_Modelo):
    sigla: str
    sistema: str
    status: str
    portal: str
    observacao: str | None = None


class MovimentacoesResposta(_Modelo):
    numero_processo: str
    tribunal: str | None
    encontrado: bool
    completas: bool
    movimentos: list[Movimentacao]
    consultado_em: datetime
    avisos: list[str] = []


def _instancias(valor: str) -> tuple[str, ...]:
    escolhidas = tuple(dict.fromkeys(v.strip() for v in valor.split(",") if v.strip() in {"1", "2"}))
    return escolhidas or ("1", "2")


def _problema(
    request: Request, status: int, codigo: str, detalhe: str, tribunal: str | None, transitorio: bool
) -> JSONResponse:
    return JSONResponse(
        status_code=status,
        media_type="application/problem+json",
        content={
            "type": f"https://github.com/MiguelFariasDev/juscraper/blob/main/docs/erros.md#{codigo.lower()}",
            "title": codigo,
            "status": status,
            "detail": detalhe,
            "instance": str(request.url.path),
            "codigo": codigo,
            "tribunal": tribunal,
            "transitorio": transitorio,
            "timestamp": datetime.now(timezone.utc).isoformat(),
        },
    )


app = criar_app()
