"""Erros tipados do juscraper.

Cada erro tem um ``codigo`` estável, que a API devolve no ProblemDetails e que o Iustitia
pode mapear para o próprio catálogo de erros. A distinção importa para quem consome:

- erro do *chamador* (número inválido, tribunal não suportado) — não adianta repetir;
- erro *transitório* do tribunal (fora do ar, lento) — vale tentar de novo mais tarde;
- *bloqueio* (captcha, firewall anti-robô) — repetir piora; o caminho é outra fonte
  ou o cadastro manual.

"Processo não encontrado" NÃO é erro: é um resultado com ``encontrado = False``, pelo mesmo
motivo do ADR-064 do Iustitia — a tela precisa distinguir "não existe lá" de "falhou".
"""

from __future__ import annotations


class JuscraperError(Exception):
    codigo = "ERRO_INTERNO"
    http_status = 500
    #: Se faz sentido o chamador tentar de novo mais tarde.
    transitorio = False

    def __init__(self, mensagem: str, *, tribunal: str | None = None) -> None:
        super().__init__(mensagem)
        self.mensagem = mensagem
        self.tribunal = tribunal


class NumeroCnjInvalido(JuscraperError):
    codigo = "NUMERO_CNJ_INVALIDO"
    http_status = 400

    def __init__(self, numero: str | None) -> None:
        super().__init__(f"Número CNJ inválido: {numero!r}.")
        self.numero = numero


class TribunalNaoSuportado(JuscraperError):
    codigo = "TRIBUNAL_NAO_SUPORTADO"
    http_status = 422


class TribunalIndisponivel(JuscraperError):
    """Falha de rede, timeout ou 5xx persistente depois das tentativas."""

    codigo = "TRIBUNAL_INDISPONIVEL"
    http_status = 502
    transitorio = True


class CaptchaExigido(JuscraperError):
    """O portal passou a exigir captcha. O juscraper não tenta resolvê-lo."""

    codigo = "CAPTCHA_EXIGIDO"
    http_status = 503


class AcessoBloqueado(JuscraperError):
    """Firewall/WAF do tribunal classificou o acesso como automatizado (403, página de bloqueio)."""

    codigo = "ACESSO_BLOQUEADO"
    http_status = 503


class RespostaInesperada(JuscraperError):
    """O HTML/JSON não tem a estrutura esperada — sinal de que o portal mudou de layout."""

    codigo = "RESPOSTA_INESPERADA"
    http_status = 502
