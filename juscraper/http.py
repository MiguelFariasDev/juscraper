"""Camada HTTP compartilhada pelos scrapers.

Três responsabilidades, na ordem em que agem sobre cada requisição:

1. **Throttling por host** — intervalo mínimo entre requisições ao mesmo portal e limite de
   requisições simultâneas. Os portais são serviços públicos sem SLA nem limite
   documentado; ir devagar é o que mantém o acesso. É também o que diferencia uma consulta
   pontual de uma varredura, que os termos de uso dos tribunais vedam.
2. **Retry com backoff exponencial e jitter** — só para falhas transitórias (rede, timeout,
   408, 429, 5xx). Respeita ``Retry-After`` quando o servidor manda.
3. **Detecção de bloqueio** — página de WAF, 403 e captcha viram erros tipados e NÃO são
   repetidos: insistir contra um bloqueio só o prolonga. Não há nenhuma tentativa de
   contornar captcha ou detecção de robô, por decisão de projeto.

O limitador vive no processo. Com mais de uma réplica da API, cada uma tem o seu orçamento
— suficiente para consultas sob demanda; para escalar, o limitador precisa ir para o Redis.
"""

from __future__ import annotations

import asyncio
import html
import logging
import random
import re
import time
from collections.abc import Mapping
from dataclasses import dataclass, field
from urllib.parse import urlsplit

import httpx

from juscraper.errors import AcessoBloqueado, CaptchaExigido, TribunalIndisponivel

log = logging.getLogger(__name__)

_STATUS_TRANSITORIOS = {408, 429, 500, 502, 503, 504}

# Marcadores de páginas de bloqueio conhecidas (WAF do TJSC, Cloudflare, F5…).
_MARCADORES_BLOQUEIO = re.compile(
    r"Bloqueio tempor[aá]rio|classificaram esse acesso como n[aã]o autorizado|"
    r"Request Rejected|Attention Required! \| Cloudflare|Support ID[^<]{0,40}_BOT\b",
    re.IGNORECASE,
)
# Captcha *ativo* na página (widget renderizado), não a mera menção em JavaScript.
_MARCADORES_CAPTCHA = re.compile(
    r'class="g-recaptcha"|class="h-captcha"|class="cf-turnstile"|'
    r"www\.google\.com/recaptcha/api\.js|hcaptcha\.com/1/api\.js|"
    r'id="captcha[_A-Za-z]*"[^>]*<img',
    re.IGNORECASE,
)

USER_AGENT_PADRAO = (
    "Mozilla/5.0 (compatible; juscraper/0.1; consulta processual pontual)"
)


@dataclass(slots=True)
class PoliticaHost:
    intervalo_minimo: float = 1.0
    """Segundos entre o início de duas requisições ao mesmo host."""
    concorrencia: int = 2


@dataclass
class ConfigHttp:
    timeout: float = 30.0
    tentativas: int = 3
    backoff_base: float = 2.0
    user_agent: str = USER_AGENT_PADRAO
    politica_padrao: PoliticaHost = field(default_factory=PoliticaHost)
    politicas: dict[str, PoliticaHost] = field(default_factory=dict)


class _LimitadorHost:
    def __init__(self, politica: PoliticaHost) -> None:
        self._politica = politica
        self._semaforo = asyncio.Semaphore(politica.concorrencia)
        self._lock = asyncio.Lock()
        self._proximo = 0.0

    async def __aenter__(self) -> None:
        await self._semaforo.acquire()
        async with self._lock:
            agora = time.monotonic()
            espera = self._proximo - agora
            self._proximo = max(agora, self._proximo) + self._politica.intervalo_minimo
        if espera > 0:
            await asyncio.sleep(espera)

    async def __aexit__(self, *exc: object) -> None:
        self._semaforo.release()


class Limitadores:
    """Um limitador por host, compartilhado entre todas as consultas do processo."""

    def __init__(self, config: ConfigHttp) -> None:
        self._config = config
        self._por_host: dict[str, _LimitadorHost] = {}

    def para(self, host: str) -> _LimitadorHost:
        if host not in self._por_host:
            politica = self._config.politicas.get(host, self._config.politica_padrao)
            self._por_host[host] = _LimitadorHost(politica)
        return self._por_host[host]


class Sessao:
    """Uma sessão HTTP (cookie jar próprio) para uma consulta.

    O e-SAJ amarra o carregamento AJAX das movimentações à sessão aberta no ``show.do``;
    por isso cada consulta usa a sua, e consultas paralelas não misturam cookies.
    """

    def __init__(
        self,
        config: ConfigHttp,
        limitadores: Limitadores,
        *,
        tribunal: str,
        transport: httpx.AsyncBaseTransport | None = None,
    ) -> None:
        self._config = config
        self._limitadores = limitadores
        self._tribunal = tribunal
        self._cliente = httpx.AsyncClient(
            timeout=config.timeout,
            follow_redirects=True,
            headers={
                "User-Agent": config.user_agent,
                "Accept-Language": "pt-BR,pt;q=0.9",
            },
            transport=transport,
        )

    async def __aenter__(self) -> Sessao:
        return self

    async def __aexit__(self, *exc: object) -> None:
        await self._cliente.aclose()

    async def get(
        self,
        url: str,
        *,
        params: Mapping[str, str | int] | None = None,
        headers: Mapping[str, str] | None = None,
    ) -> httpx.Response:
        host = urlsplit(url).hostname or ""
        ultima_falha: str = ""

        for tentativa in range(1, self._config.tentativas + 1):
            async with self._limitadores.para(host):
                try:
                    resposta = await self._cliente.get(url, params=params, headers=headers)
                except (httpx.TimeoutException, httpx.TransportError) as exc:
                    ultima_falha = f"{type(exc).__name__}: {exc}"
                    resposta = None

            if resposta is not None:
                self._verificar_bloqueio(resposta)
                if resposta.status_code not in _STATUS_TRANSITORIOS:
                    resposta.raise_for_status()
                    return resposta
                ultima_falha = f"HTTP {resposta.status_code}"

            if tentativa < self._config.tentativas:
                espera = self._espera(tentativa, resposta)
                log.warning(
                    "Falha transitória em %s (%s); tentativa %d/%d em %.1fs",
                    host, ultima_falha, tentativa, self._config.tentativas, espera,
                )
                await asyncio.sleep(espera)

        raise TribunalIndisponivel(
            f"{host} não respondeu após {self._config.tentativas} tentativas ({ultima_falha}).",
            tribunal=self._tribunal,
        )

    async def get_json(self, url: str, **kwargs: object) -> object:
        resposta = await self.get(url, **kwargs)  # type: ignore[arg-type]
        return resposta.json()

    def _espera(self, tentativa: int, resposta: httpx.Response | None) -> float:
        if resposta is not None and (retry_after := resposta.headers.get("Retry-After")):
            try:
                return min(float(retry_after), 60.0)
            except ValueError:
                pass
        base = self._config.backoff_base * 2 ** (tentativa - 1)
        # Jitter "full": evita que consultas paralelas repitam em rajada sincronizada.
        return random.uniform(base / 2, base)

    def _verificar_bloqueio(self, resposta: httpx.Response) -> None:
        host = resposta.url.host
        tipo = resposta.headers.get("content-type", "")
        corpo = resposta.text if ("html" in tipo or "text" in tipo or not tipo) else ""

        # Páginas de bloqueio costumam vir com entidades ("tempor&aacute;rio").
        if corpo and _MARCADORES_BLOQUEIO.search(html.unescape(corpo[:20000])):
            raise AcessoBloqueado(
                f"O portal {host} bloqueou o acesso automatizado (página de bloqueio do firewall).",
                tribunal=self._tribunal,
            )
        if resposta.status_code == 403:
            raise AcessoBloqueado(f"O portal {host} recusou o acesso (HTTP 403).", tribunal=self._tribunal)
        if corpo and _MARCADORES_CAPTCHA.search(corpo):
            raise CaptchaExigido(
                f"O portal {host} exige captcha para esta consulta; o juscraper não resolve captchas.",
                tribunal=self._tribunal,
            )
