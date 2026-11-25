"""Scraper do e-SAJ (Softplan) — TJSP, TJAL, TJCE, TJAC, TJMS, TJAM.

Fluxo, como observado nos portais em novembro de 2026:

1. ``{cpopg|cposg}/search.do`` com o número unificado. Um único resultado redireciona
   direto para ``show.do``; vários (comum no 2º grau: apelação + embargos) caem numa
   lista; nenhum devolve ``#mensagemRetorno`` "Não existem informações disponíveis…".
2. ``show.do`` traz dados principais e partes. As movimentações vêm em DOIS formatos,
   conforme a versão do e-SAJ instalada no tribunal:

   - **inline** (versões antigas, ex.: TJAL): ``tbody#tabelaTodasMovimentacoes``;
   - **sob demanda** (TJSP, e-SAJ 26.x): botão "Exibir movimentações" que chama
     ``carregarMovimentacoesAjax.do``, 5 linhas por vez, com um cursor cifrado (keyset)
     para a página seguinte. O 1º e o 2º grau usam parâmetros e marcadores diferentes:

     ======== =================== ================================================
     grau     parâmetro           onde está o próximo cursor
     ======== =================== ================================================
     cpopg    ``processo.codigo`` ``input#cursorMovimentacoesPaginado`` (1ª pág.),
                                  ``tr#trCursorMovimentacoes[data-cursor]`` (demais)
     cposg    ``cdProcesso``      ``a#linkMaisRecolherMovimentacoes`` (1ª pág.),
                                  ``tr#metaPaginaMovimentacoes`` (demais)
     ======== =================== ================================================

As funções ``parse_*`` são puras (HTML → dados) para serem testadas com fixtures, sem rede.
"""

from __future__ import annotations

import logging
import re
from dataclasses import dataclass
from typing import TYPE_CHECKING
from urllib.parse import urljoin

from bs4 import BeautifulSoup, NavigableString, Tag

from juscraper.errors import JuscraperError, RespostaInesperada
from juscraper.models import Advogado, Movimentacao, Parte, Polo, Processo
from juscraper.scrapers.base import (
    OpcoesConsulta,
    ResultadoScraper,
    Scraper,
    data_br,
    data_hora_br,
    inferir_polo,
    limpar,
)

if TYPE_CHECKING:
    from juscraper.cnj import NumeroCnj
    from juscraper.http import Sessao

log = logging.getLogger(__name__)

SISTEMA = "e-SAJ"

_NAO_ENCONTRADO = re.compile(r"N[ãa]o existem informa[çc][õo]es dispon[íi]veis", re.IGNORECASE)
_POPUP_SENHA_ATIVO = re.compile(r"popupSenha\s*:\s*\{\s*mostrar\s*:\s*'true'\s*==\s*'true'")
_CODIGO_CPOPG = re.compile(r"\$\.saj\.movimentacoes\s*=\s*\{\s*codigoProcesso\s*:\s*'([^']+)'")
_CODIGO_CPOSG = re.compile(r"var\s+cdProcessoMovimentacoes\s*=\s*'([^']+)'")
_LINK_SHOW = re.compile(r"show\.do\?processo\.codigo=([A-Z0-9]+)")
_MAX_PROCESSOS_POR_NUMERO = 10


@dataclass(frozen=True, slots=True)
class Instancia:
    chave: str  # "1" ou "2"
    caminho: str  # cpopg, cposg, cpopg5…
    grau: str  # G1, G2

    @property
    def segundo_grau(self) -> bool:
        return self.chave == "2"


@dataclass(slots=True)
class PaginaDetalhe:
    processo: Processo
    #: Código interno do e-SAJ, quando as movimentações são carregadas sob demanda.
    codigo_ajax: str | None = None


class EsajScraper(Scraper):
    sistema = SISTEMA

    def __init__(
        self,
        tribunal: str,
        base_url: str,
        *,
        primeiro_grau: str = "cpopg",
        segundo_grau: str | None = "cposg",
    ) -> None:
        super().__init__(tribunal)
        self.base_url = base_url.rstrip("/")
        self.instancias = {"1": Instancia("1", primeiro_grau, "G1")}
        if segundo_grau:
            self.instancias["2"] = Instancia("2", segundo_grau, "G2")

    async def consultar(
        self, numero: NumeroCnj, sessao: Sessao, opcoes: OpcoesConsulta
    ) -> ResultadoScraper:
        processos: list[Processo] = []
        avisos: list[str] = []
        falhas: list[JuscraperError] = []
        alvos = [self.instancias[c] for c in opcoes.instancias if c in self.instancias]

        for instancia in alvos:
            try:
                processos.extend(await self._consultar_instancia(numero, instancia, sessao, opcoes))
            except JuscraperError as exc:
                # Uma instância fora do ar não deve esconder o que a outra respondeu.
                falhas.append(exc)
                avisos.append(f"{instancia.grau}: {exc.mensagem}")

        if alvos and len(falhas) == len(alvos):
            raise falhas[0]

        return ResultadoScraper(processos, avisos)

    # -- fluxo HTTP -------------------------------------------------------------------

    async def _consultar_instancia(
        self, numero: NumeroCnj, instancia: Instancia, sessao: Sessao, opcoes: OpcoesConsulta
    ) -> list[Processo]:
        url = f"{self.base_url}/{instancia.caminho}/search.do"
        resposta = await sessao.get(url, params=_parametros_busca(numero, instancia))
        html = resposta.text

        match classificar_pagina(html):
            case "nao_encontrado":
                return []
            case "lista":
                codigos = parse_lista(html)[:_MAX_PROCESSOS_POR_NUMERO]
                processos = []
                for codigo in codigos:
                    show = await sessao.get(
                        f"{self.base_url}/{instancia.caminho}/show.do",
                        params={"processo.codigo": codigo},
                    )
                    processos.append(
                        await self._completar(show.text, str(show.url), instancia, sessao, opcoes)
                    )
                return processos
            case _:
                return [await self._completar(html, str(resposta.url), instancia, sessao, opcoes)]

    async def _completar(
        self, html: str, url: str, instancia: Instancia, sessao: Sessao, opcoes: OpcoesConsulta
    ) -> Processo:
        pagina = parse_detalhe(html, tribunal=self.tribunal, grau=instancia.grau)
        processo = pagina.processo
        processo.url_consulta = url

        if pagina.codigo_ajax and not processo.em_segredo_de_justica:
            movimentos, completos = await self._movimentacoes_ajax(
                pagina.codigo_ajax, instancia, sessao, opcoes
            )
            processo.movimentos = movimentos
            processo.movimentos_completos = completos
        elif opcoes.max_movimentacoes is not None and len(processo.movimentos) > opcoes.max_movimentacoes:
            processo.movimentos = processo.movimentos[: opcoes.max_movimentacoes]
            processo.movimentos_completos = False

        for movimento in processo.movimentos:
            movimento.instancia = instancia.grau
        return processo

    async def _movimentacoes_ajax(
        self, codigo: str, instancia: Instancia, sessao: Sessao, opcoes: OpcoesConsulta
    ) -> tuple[list[Movimentacao], bool]:
        url = urljoin(f"{self.base_url}/{instancia.caminho}/", "carregarMovimentacoesAjax.do")
        chave = "cdProcesso" if instancia.segundo_grau else "processo.codigo"
        movimentos: list[Movimentacao] = []
        cursor: str | None = None
        vistos: set[str] = set()

        for _ in range(opcoes.max_paginas_movimentacoes):
            params = {chave: codigo}
            if cursor:
                params["cursor"] = cursor
            resposta = await sessao.get(url, params=params, headers={"X-Requested-With": "XMLHttpRequest"})
            soup = BeautifulSoup(resposta.text, "lxml")
            movimentos.extend(parse_linhas_movimentacao(soup))

            if opcoes.max_movimentacoes is not None and len(movimentos) >= opcoes.max_movimentacoes:
                return movimentos[: opcoes.max_movimentacoes], False

            cursor = proximo_cursor(soup)
            if not cursor:
                return movimentos, True
            if cursor in vistos:  # defesa contra loop se o portal repetir o cursor
                log.warning("e-SAJ %s repetiu o cursor de movimentações; interrompendo.", self.tribunal)
                return movimentos, False
            vistos.add(cursor)

        return movimentos, False


# -- parâmetros -----------------------------------------------------------------------


def _parametros_busca(numero: NumeroCnj, instancia: Instancia) -> list[tuple[str, str]]:
    comuns = [
        ("conversationId", ""),
        ("cbPesquisa", "NUMPROC"),
        ("numeroDigitoAnoUnificado", numero.numero_digito_ano),
        ("foroNumeroUnificado", numero.origem),
    ]
    if instancia.segundo_grau:
        return comuns + [
            ("paginaConsulta", "0"),
            ("dePesquisaNuUnificado", numero.formatado),
            ("dePesquisaNuUnificado", "UNIFICADO"),
            ("dePesquisa", ""),
            ("tipoNuProcesso", "UNIFICADO"),
        ]
    return comuns + [
        ("dadosConsulta.valorConsultaNuUnificado", numero.formatado),
        ("dadosConsulta.valorConsultaNuUnificado", "UNIFICADO"),
        ("dadosConsulta.valorConsulta", ""),
        ("dadosConsulta.tipoNuProcesso", "UNIFICADO"),
    ]


# -- parsers (puros) ------------------------------------------------------------------


def classificar_pagina(html: str) -> str:
    """``nao_encontrado`` | ``segredo`` | ``detalhe`` | ``lista``."""
    soup = BeautifulSoup(html, "lxml")
    mensagem = soup.find(id="mensagemRetorno")
    if mensagem and _NAO_ENCONTRADO.search(mensagem.get_text()):
        return "nao_encontrado"
    if _POPUP_SENHA_ATIVO.search(html):
        return "segredo"
    numero = soup.find(id="numeroProcesso")
    if numero and limpar(numero.get_text()):
        return "detalhe"
    if parse_lista(html):
        return "lista"
    if mensagem:
        raise RespostaInesperada(f"e-SAJ respondeu: {limpar(mensagem.get_text())}")
    raise RespostaInesperada("Página do e-SAJ sem dados de processo, lista ou mensagem reconhecível.")


def parse_lista(html: str) -> list[str]:
    """Códigos internos dos processos de uma página de lista, na ordem e sem repetição."""
    codigos = _LINK_SHOW.findall(html)
    soup = BeautifulSoup(html, "lxml")
    codigos += [i.get("value", "") for i in soup.select('input[name="processoSelecionado"]')]
    return list(dict.fromkeys(c for c in codigos if c))


def parse_detalhe(html: str, *, tribunal: str, grau: str) -> PaginaDetalhe:
    soup = BeautifulSoup(html, "lxml")

    numero = _texto(soup, "numeroProcesso")
    if _POPUP_SENHA_ATIVO.search(html):
        return PaginaDetalhe(
            Processo(
                numero_processo=numero or "",
                tribunal=tribunal,
                grau=grau,
                sistema=SISTEMA,
                em_segredo_de_justica=True,
            )
        )
    if not numero:
        raise RespostaInesperada("Página de detalhe do e-SAJ sem #numeroProcesso — layout mudou?")

    # As etiquetas de situação ("Extinto", "Baixado"…) são irmãs do número; o contêiner
    # muda entre versões do e-SAJ, por isso a busca parte do próprio #numeroProcesso.
    elemento_numero = soup.find(id="numeroProcesso")
    situacoes = [
        t
        for t in (limpar(s.get_text()) for s in elemento_numero.parent.select(".unj-tag"))
        if t
    ] if isinstance(elemento_numero, Tag) and elemento_numero.parent else []
    assunto = _texto(soup, "assuntoProcesso")
    distribuicao = _texto(soup, "dataHoraDistribuicaoProcesso")
    origem = _rotulado(soup, "Origem")

    processo = Processo(
        numero_processo=numero,
        tribunal=tribunal,
        grau=grau,
        sistema=SISTEMA,
        data_ajuizamento=data_br(distribuicao),
        classe=_texto(soup, "classeProcesso"),
        orgao_julgador=_texto(soup, "orgaoJulgadorProcesso") or _texto(soup, "varaProcesso"),
        assuntos=[assunto] if assunto else [],
        situacao=", ".join(situacoes) or _texto(soup, "situacaoProcesso"),
        area=_texto(soup, "areaProcesso"),
        foro=_texto(soup, "foroProcesso") or origem,
        juiz=_texto(soup, "juizProcesso") or _texto(soup, "relatorProcesso"),
        valor_da_causa=_texto(soup, "valorAcaoProcesso"),
        partes=parse_partes(soup),
    )

    codigo_ajax = None
    if soup.find(id="btnExibirMovimentacoes") and (m := _CODIGO_CPOPG.search(html)):
        codigo_ajax = m.group(1)
    elif soup.find(id="linkExibirMovimentacoes") and (m := _CODIGO_CPOSG.search(html)):
        codigo_ajax = m.group(1)
    else:
        corpo = soup.find(id="tabelaTodasMovimentacoes") or soup.find(id="tabelaUltimasMovimentacoes")
        processo.movimentos = parse_linhas_movimentacao(corpo) if corpo else []

    return PaginaDetalhe(processo, codigo_ajax)


def parse_partes(soup: BeautifulSoup) -> list[Parte]:
    # "Todas as partes" fica oculta quando há muitas; quando existe, é a lista completa
    # (a de "principais" é um subconjunto e duplicaria nomes).
    tabela = soup.find(id="tableTodasPartes") or soup.find(id="tablePartesPrincipais")
    if not isinstance(tabela, Tag):
        return []

    partes = []
    for linha in tabela.find_all("tr"):
        rotulo = linha.find("td", class_="label")
        celula = linha.find("td", class_="nomeParteEAdvogado")
        if not rotulo or not celula:
            continue
        tipo = (limpar(rotulo.get_text()) or "").rstrip(":").strip()
        nome, advogados = _nome_e_advogados(celula)
        if not nome:
            continue
        classes = linha.get("class") or []
        polo = (
            Polo.ATIVO if "poloAtivo" in classes
            else Polo.PASSIVO if "poloPassivo" in classes
            else inferir_polo(tipo)
        )
        partes.append(Parte(nome=nome, tipo=tipo, polo=polo, advogados=advogados))
    return partes


def _nome_e_advogados(celula: Tag) -> tuple[str | None, list[Advogado]]:
    """A célula mistura nome da parte, rótulos ("Advogado:") e nomes de advogados como
    nós irmãos — não há um elemento por advogado. Uma pequena máquina de estados resolve."""
    nome_partes: list[str] = []
    advogados: list[Advogado] = []
    buffer: list[str] = []
    em_advogado = False

    def descarregar() -> None:
        texto = limpar(" ".join(buffer))
        buffer.clear()
        if not texto:
            return
        if em_advogado:
            advogados.append(Advogado(nome=texto))
        else:
            nome_partes.append(texto)

    for no in celula.children:
        if isinstance(no, NavigableString):
            buffer.append(str(no))
        elif isinstance(no, Tag):
            if no.name == "span" and "mensagemExibindo" in (no.get("class") or []):
                descarregar()
                em_advogado = True
            elif no.name == "br":
                descarregar()
            elif no.name == "input":
                descarregar()
                if advogados and (oab := limpar(no.get("value"))):
                    advogados[-1].oab = oab
            else:
                buffer.append(no.get_text(" "))
    descarregar()

    return (limpar(" ".join(nome_partes)), advogados)


def parse_linhas_movimentacao(raiz: Tag | BeautifulSoup) -> list[Movimentacao]:
    movimentos = []
    for linha in raiz.select("tr.containerMovimentacao, tr.movimentacaoProcesso"):
        data = linha.find("td", class_=re.compile(r"^dataMovimentacao"))
        descricao = linha.find("td", class_=re.compile(r"^descricaoMovimentacao"))
        if not descricao:
            continue

        italico = descricao.find("span", style=re.compile(r"italic"))
        complemento = limpar(italico.get_text(" ")) if italico else None
        if italico:
            italico.extract()
        nome = limpar(descricao.get_text(" "))
        if not nome:
            continue

        movimentos.append(
            Movimentacao(
                nome=nome,
                data_hora=data_hora_br(data.get_text() if data else None),
                complemento=complemento,
                possui_documento=linha.find("a", class_="linkMovVincProc") is not None,
            )
        )
    return movimentos


def proximo_cursor(soup: BeautifulSoup) -> str | None:
    """Cursor da próxima página de movimentações, ou None se esta foi a última."""
    meta = soup.find(id="metaPaginaMovimentacoes")
    if isinstance(meta, Tag):
        return meta.get("data-cursor") if meta.get("data-tem-mais-paginas") == "true" else None

    for elemento, atributo in (
        (soup.find(id="trCursorMovimentacoes"), "data-cursor"),
        (soup.find(id="cursorMovimentacoesPaginado"), "value"),
        (soup.find(id="linkMaisRecolherMovimentacoes"), "data-cursor"),
    ):
        if isinstance(elemento, Tag) and (valor := elemento.get(atributo)):
            return str(valor)
    return None


# -- helpers --------------------------------------------------------------------------


def _texto(soup: BeautifulSoup, id_: str) -> str | None:
    elemento = soup.find(id=id_)
    return limpar(elemento.get_text(" ")) if isinstance(elemento, Tag) else None


def _rotulado(soup: BeautifulSoup, rotulo: str) -> str | None:
    """Valor de um campo sem id, localizado pelo rótulo ``span.unj-label``."""
    for label in soup.select("span.unj-label"):
        if limpar(label.get_text()) == rotulo and (valor := label.find_next_sibling()):
            return limpar(valor.get_text(" "))
    return None
