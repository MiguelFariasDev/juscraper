"""Registro dos tribunais: qual sistema cada um usa e em que estado está a integração.

``status``:

- ``validado`` — fluxo confirmado com processo real (nov/2026), com fixture em ``tests/``;
- ``experimental`` — mesmo sistema de um validado, portal respondeu, mas sem processo real
  conferido; pode funcionar como está ou exigir ajuste fino;
- ``indisponivel`` — o portal bloqueia acesso automatizado ou exige captcha. Fica
  registrado para que a API responda com o motivo, em vez de "não suportado" genérico.
"""

from __future__ import annotations

from collections.abc import Callable
from dataclasses import dataclass

from juscraper.errors import AcessoBloqueado, CaptchaExigido, JuscraperError, TribunalNaoSuportado
from juscraper.scrapers.base import Scraper
from juscraper.scrapers.esaj import EsajScraper
from juscraper.scrapers.pje import PjeConsultaPublicaScraper


@dataclass(frozen=True, slots=True)
class Tribunal:
    sigla: str
    sistema: str
    status: str
    portal: str
    fabrica: Callable[[], Scraper] | None = None
    observacao: str | None = None
    erro: type[JuscraperError] | None = None

    def scraper(self) -> Scraper:
        if self.fabrica is None:
            erro = self.erro or TribunalNaoSuportado
            raise erro(
                f"{self.sigla} ({self.sistema}) não está disponível: {self.observacao}",
                tribunal=self.sigla,
            )
        return self.fabrica()


def _esaj(sigla: str, base: str, status: str, primeiro: str = "cpopg", segundo: str | None = "cposg",
          observacao: str | None = None) -> Tribunal:
    return Tribunal(
        sigla, "e-SAJ", status, base,
        lambda: EsajScraper(sigla, base, primeiro_grau=primeiro, segundo_grau=segundo),
        observacao,
    )


TRIBUNAIS: dict[str, Tribunal] = {
    t.sigla: t
    for t in [
        _esaj("TJSP", "https://esaj.tjsp.jus.br", "validado",
              observacao="1º e 2º grau; movimentações sob demanda (AJAX paginado)."),
        _esaj("TJAL", "https://www2.tjal.jus.br", "validado", "cpopg", "cposg5",
              observacao="1º grau validado (movimentações inline); 2º grau em /cposg5 "
                         "(o antigo /cposg redireciona para o portal novo)."),
        _esaj("TJCE", "https://esaj.tjce.jus.br", "experimental", "cpopg", "cposg5",
              observacao="/cposg respondeu 404 nos testes; 2º grau configurado em /cposg5."),
        _esaj("TJAC", "https://esaj.tjac.jus.br", "experimental",
              observacao="Parte do acervo do TJAC está no Projudi, que não é coberto."),
        _esaj("TJMS", "https://esaj.tjms.jus.br", "experimental", "cpopg5", "cposg5",
              observacao="Portal não respondeu nos testes (timeout)."),
        _esaj("TJAM", "https://consultasaj.tjam.jus.br", "experimental", "cpopg", "cposgcr",
              observacao="Portal recusou conexões vindas de fora da rede brasileira nos testes."),
        Tribunal(
            "TJDFT", "PJe", "validado", "https://pje-consultapublica.tjdft.jus.br",
            lambda: PjeConsultaPublicaScraper(
                "TJDFT", "https://pje-consultapublica-api.tjdft.jus.br",
                grau="G1", url_portal="https://pje-consultapublica.tjdft.jus.br",
            ),
            "Nova consulta pública do PJe (API REST).",
        ),
        Tribunal("TJMG", "PJe", "indisponivel", "https://pje-consulta-publica.tjmg.jus.br",
                 observacao="a consulta pública exige captcha.", erro=CaptchaExigido),
        Tribunal("TJBA", "PJe", "indisponivel", "https://consultapublicapje.tjba.jus.br",
                 observacao="o portal recusou as conexões nos testes.", erro=AcessoBloqueado),
        Tribunal("TJSC", "eproc", "indisponivel", "https://eproc1g.tjsc.jus.br",
                 observacao="o firewall do tribunal bloqueia acesso automatizado.", erro=AcessoBloqueado),
        Tribunal("TJRS", "eproc", "indisponivel", "https://eproc1g.tjrs.jus.br",
                 observacao="o portal responde 403 a acesso automatizado.", erro=AcessoBloqueado),
    ]
}


def obter(sigla: str | None) -> Tribunal:
    if sigla is None:
        raise TribunalNaoSuportado("O número CNJ não corresponde a nenhum tribunal existente.")
    tribunal = TRIBUNAIS.get(sigla)
    if tribunal is None:
        suportados = ", ".join(s for s, t in TRIBUNAIS.items() if t.fabrica)
        raise TribunalNaoSuportado(
            f"{sigla} ainda não tem scraper. Suportados: {suportados}.", tribunal=sigla
        )
    return tribunal
