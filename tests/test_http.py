import httpx
import pytest

from juscraper.errors import AcessoBloqueado, CaptchaExigido, TribunalIndisponivel
from juscraper.http import Limitadores, Sessao

from .conftest import FIXTURES, config_rapida


def _sessao(handler) -> Sessao:
    config = config_rapida()
    return Sessao(config, Limitadores(config), tribunal="TJXX", transport=httpx.MockTransport(handler))


async def test_retry_em_falha_transitoria():
    chamadas = []

    def handler(request):
        chamadas.append(request)
        return httpx.Response(503) if len(chamadas) < 3 else httpx.Response(200, text="ok")

    async with _sessao(handler) as sessao:
        resposta = await sessao.get("https://portal.jus.br/x")
    assert resposta.text == "ok"
    assert len(chamadas) == 3


async def test_desiste_apos_tentativas_e_marca_transitorio():
    async with _sessao(lambda r: httpx.Response(500)) as sessao:
        with pytest.raises(TribunalIndisponivel) as erro:
            await sessao.get("https://portal.jus.br/x")
    assert erro.value.transitorio


async def test_403_nao_e_repetido():
    chamadas = []

    def handler(request):
        chamadas.append(request)
        return httpx.Response(403, text="Forbidden")

    async with _sessao(handler) as sessao:
        with pytest.raises(AcessoBloqueado):
            await sessao.get("https://portal.jus.br/x")
    assert len(chamadas) == 1, "insistir contra bloqueio só o prolonga"


async def test_pagina_de_bloqueio_do_waf_com_status_200():
    html = (FIXTURES / "bloqueio_waf_tjsc.html").read_text(encoding="utf-8", errors="replace")
    async with _sessao(lambda r: httpx.Response(200, text=html, headers={"content-type": "text/html"})) as sessao:
        with pytest.raises(AcessoBloqueado):
            await sessao.get("https://eproc1g.tjsc.jus.br/eproc/")


async def test_captcha_detectado_e_nao_resolvido():
    html = '<form><div class="g-recaptcha" data-sitekey="x"></div></form>'
    async with _sessao(lambda r: httpx.Response(200, text=html, headers={"content-type": "text/html"})) as sessao:
        with pytest.raises(CaptchaExigido):
            await sessao.get("https://portal.jus.br/x")


async def test_mencao_a_captcha_em_javascript_nao_e_captcha():
    # O e-SAJ tem "uuidCaptcha" no JS de toda página de processo, sem captcha nenhum.
    html = "<script>var captcha = $.saj.getUrlParameter('uuidCaptcha');</script><span id='numeroProcesso'>1</span>"
    async with _sessao(lambda r: httpx.Response(200, text=html, headers={"content-type": "text/html"})) as sessao:
        resposta = await sessao.get("https://portal.jus.br/x")
    assert resposta.status_code == 200
