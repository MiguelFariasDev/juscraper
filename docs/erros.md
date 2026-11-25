# Erros da API

Todo erro vem como ProblemDetails (RFC 7807), `Content-Type: application/problem+json`:

```json
{
  "type": "https://github.com/MiguelFariasDev/juscraper/blob/main/docs/erros.md#captcha_exigido",
  "title": "CAPTCHA_EXIGIDO",
  "status": 503,
  "detail": "TJMG (PJe) não está disponível: a consulta pública exige captcha.",
  "instance": "/v1/processos/5001825-87.2026.8.13.0439",
  "codigo": "CAPTCHA_EXIGIDO",
  "tribunal": "TJMG",
  "transitorio": false,
  "timestamp": "2026-11-25T01:52:56Z"
}
```

`transitorio: true` significa que vale tentar de novo mais tarde. `false` significa que
repetir não muda nada.

**Processo não encontrado não é erro**: a resposta é `200` com `"encontrado": false`.

| Código | HTTP | Transitório | Significado | O que fazer |
|---|---|---|---|---|
| <a id="numero_cnj_invalido"></a>`NUMERO_CNJ_INVALIDO` | 400 | não | Número sem 20 dígitos, segmento 0 ou dígito verificador errado | Corrigir o número |
| <a id="tribunal_nao_suportado"></a>`TRIBUNAL_NAO_SUPORTADO` | 422 | não | O número é válido, mas o tribunal ainda não tem scraper | Cadastro manual ou outra fonte |
| <a id="tribunal_indisponivel"></a>`TRIBUNAL_INDISPONIVEL` | 502 | **sim** | O portal não respondeu após as tentativas (rede, timeout, 5xx) | Tentar mais tarde |
| <a id="resposta_inesperada"></a>`RESPOSTA_INESPERADA` | 502 | não | O portal respondeu numa estrutura desconhecida, provavelmente mudou de layout | Rodar `pytest -m live`, regravar o cassete e ajustar o parser |
| <a id="captcha_exigido"></a>`CAPTCHA_EXIGIDO` | 503 | não | O portal exige captcha. O juscraper não resolve captchas | Outra fonte ou cadastro manual |
| <a id="acesso_bloqueado"></a>`ACESSO_BLOQUEADO` | 503 | não | Firewall do tribunal bloqueou o acesso automatizado (403 ou página de bloqueio) | Não insistir. Outra fonte ou cadastro manual |
| <a id="nao_autorizado"></a>`NAO_AUTORIZADO` | 401 | não | `X-Api-Key` ausente ou inválida | Configurar a chave |

## Resultado parcial

No e-SAJ, 1º e 2º grau são consultados separadamente. Se uma instância falhar e a outra
responder, a consulta é **sucesso** com a falha descrita em `avisos`, e esse resultado
**não** entra no cache. Só quando todas as instâncias falham é que a API devolve erro.
