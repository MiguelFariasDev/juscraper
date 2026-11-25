# juscraper

Consulta processos e **movimentações processuais** diretamente nos portais públicos dos
tribunais de justiça e devolve tudo em JSON padronizado. Foi feito para alimentar o
[Iustitia](https://github.com/MiguelFariasDev/Iustitia) como alternativa ao DataJud
(ver `integrations/iustitia/`), mas funciona sozinho: como biblioteca Python, linha de
comando ou API HTTP.

```bash
juscraper consultar 0000248-42.2024.8.26.0319
```

```
0000248-42.2024.8.26.0319 · TJSP G1 · e-SAJ
  Classe        Procedimento Comum Cível
  Órgão         2ª Vara
  Situação      Extinto
  Distribuição  2024-02-09
  Reqte         …
  Movimentações: 52
    09/04/2025  Arquivado Definitivamente
    …
```

## Cobertura

| Tribunal | Sistema | Estado | Observação |
|---|---|---|---|
| TJSP | e-SAJ | **validado** | 1º e 2º grau; movimentações via AJAX paginado por cursor |
| TJAL | e-SAJ | **validado** | 1º grau com movimentações inline; 2º grau em `/cposg5` |
| TJDFT | PJe | **validado** | nova consulta pública (API REST) |
| TJCE | e-SAJ | experimental | portal respondeu; sem processo real conferido |
| TJAC | e-SAJ | experimental | parte do acervo está no Projudi, que não é coberto |
| TJMS | e-SAJ | experimental | portal não respondeu nos testes (timeout) |
| TJAM | e-SAJ | experimental | portal recusou conexões de fora do Brasil nos testes |
| TJMG, TJBA, TJPE, TJMA, TJRO | PJe | indisponível | consulta pública **antiga** (JSF), com reCAPTCHA |
| TJPB, TJAP | PJe | indisponível | firewall anti-robô da Cloudflare |
| TJES, TJRN | PJe | indisponível | **bloqueio geográfico**: recusam acessos de fora do Brasil |
| TJSC | eproc | indisponível | firewall bloqueia acesso automatizado |
| TJRS | eproc | indisponível | HTTP 403 para acesso automatizado |

### Mais tribunais PJe: o que se sabe (nov/2026)

A nova consulta pública do PJe (Angular + API REST, sem captcha) só foi encontrada no
**TJDFT**. Ela parece ser uma implantação do próprio tribunal, não um padrão distribuído
pelo CNJ. Os demais TJs com PJe que responderam ainda usam a consulta antiga com
reCAPTCHA, que não é suportada.

Quando um tribunal migrar, não é preciso nova versão do juscraper:

```bash
# 1. a partir de um servidor no Brasil
python tools/detectar_consulta_pje.py https://<portal-de-consulta> --sigla TJXX
# 2. a linha que ele imprime vai para o ambiente
JUSCRAPER_PJE_REST="TJXX=https://<api>|https://<portal>"
```

### Hospede no Brasil

TJES, TJRN e provavelmente TJAM e TJMS recusam conexões de fora do país. Rode o juscraper
numa região brasileira (Azure *Brazil South*, onde o Iustitia já está) e refaça os testes
desses tribunais com `tools/detectar_consulta_pje.py` e `pytest -m live`.

*Validado* significa fluxo confirmado com processo real em novembro de 2026 e coberto por
teste de regressão com cassete gravado. *Experimental* é o mesmo sistema de um validado:
pode funcionar como está ou precisar de ajuste fino. `GET /v1/tribunais` devolve esta tabela.

Os indisponíveis ficam registrados de propósito: a API responde com o **motivo**
(`CAPTCHA_EXIGIDO`, `ACESSO_BLOQUEADO`) em vez de um "não suportado" genérico.

### O que o juscraper não faz, por decisão de projeto

O juscraper **não resolve captcha, não contorna firewall e não se disfarça de navegador**.
Quando um portal exige captcha ou bloqueia robôs, isso é uma escolha explícita do tribunal,
e contorná-la viola os termos de uso e deixa o sistema juridicamente frágil. Nesses casos
o caminho é outra fonte (provedor comercial, DataJud) ou o cadastro manual.

Pelo mesmo motivo, o juscraper consulta **um processo por vez, a pedido**. Não há busca por
nome, por OAB nem varredura em lote.

## Como funciona

### e-SAJ (Softplan): TJSP, TJAL, TJCE, TJAC, TJMS, TJAM

1. `GET /{cpopg|cposg}/search.do` com o número unificado. Com um resultado, redireciona
   para `show.do`; com vários (comum no 2º grau), mostra uma lista; sem nenhum, responde
   `#mensagemRetorno` "Não existem informações disponíveis".
2. `show.do` traz dados principais e partes. As **movimentações** vêm em dois formatos,
   conforme a versão do e-SAJ instalada:
   - **inline** (TJAL): `tbody#tabelaTodasMovimentacoes`;
   - **sob demanda** (TJSP, e-SAJ 26.x): `carregarMovimentacoesAjax.do`, 5 por página, com
     cursor cifrado para a próxima. O 1º e o 2º grau usam parâmetros diferentes
     (`processo.codigo` × `cdProcesso`) e marcadores de paginação diferentes.

A sessão (cookie) aberta no `show.do` é reaproveitada nas chamadas AJAX. Cada consulta
tem a sua sessão.

### PJe: nova consulta pública (TJDFT)

Os tribunais que migraram para a nova consulta pública (front-end Angular) expõem a API
REST que o próprio front-end consome. Ela foi descoberta lendo `assets/env.js` e os bundles:

```
GET {api}/v1/processos?numeroProcesso=…       → idProcesso (token opaco, muda a cada busca)
GET {api}/v1/processos/{id}/dados
GET {api}/v1/processos/{id}/poloAtivo|poloPassivo|outrosInteressados?page=N
GET {api}/v1/processos/{id}/movimentacoes?page=N   (15 por página, 0-based)
```

Para incluir outro tribunal PJe que já tenha migrado, basta uma linha em
`juscraper/tribunais.py` com o `apiUrl` do `env.js` dele, mais a liberação do host na rede.

### Cuidados com o tribunal (e com o acesso)

Os portais são serviços públicos sem SLA nem limite de uso documentado. A camada HTTP
(`juscraper/http.py`) faz, nesta ordem:

- **throttling por host**: 1 s entre requisições e no máximo 2 simultâneas por portal,
  configurável;
- **retry com backoff exponencial e jitter**, só para falha transitória (rede, timeout,
  408, 429, 5xx) e respeitando `Retry-After`;
- **detecção de bloqueio**: 403, página de WAF ou widget de captcha viram erro tipado e
  **não** são repetidos, porque insistir contra um bloqueio só o prolonga.

O custo é latência. Uma consulta fria do TJSP com 50 movimentações faz ~12 requisições
(~13 s). Com `max_movimentacoes=10` caem para 2 ou 3. Há **cache** de 30 min por padrão,
incluindo o "não encontrado".

## Uso

### Instalação

```bash
python -m venv .venv && . .venv/bin/activate
pip install -e ".[dev]"
```

### Biblioteca

```python
import asyncio
from juscraper import Juscraper

resultado = asyncio.run(Juscraper().consultar("1003603-09.2025.8.26.0319"))
print(resultado.encontrado)                    # False é desfecho normal, não erro
for processo in resultado.processos:           # uma entrada por instância (G1, G2…)
    print(processo.grau, processo.classe, len(processo.movimentos))
for m in resultado.movimentos_consolidados:    # todas as instâncias, mais recente primeiro
    print(m.data_hora, m.instancia, m.nome, m.complemento)
```

### API HTTP

```bash
cp .env.example .env      # defina JUSCRAPER_API_KEYS
juscraper serve --port 8000
# ou: docker build -t juscraper . && docker run -p 8000:8000 --env-file .env juscraper
```

| Endpoint | Descrição |
|---|---|
| `GET /health` | liveness (sem autenticação) |
| `GET /v1/tribunais` | cobertura e estado de cada tribunal |
| `GET /v1/processos/{numero}` | consulta completa. Query: `instancias=1,2`, `max_movimentacoes`, `sem_cache` |
| `GET /v1/processos/{numero}/movimentacoes` | só andamentos, consolidados. Query: `desde=AAAA-MM-DD` para acompanhamento incremental: a paginação **para** ao passar da data |

Cada movimentação tem um `id` **estável entre consultas** (hash de processo, instância,
data, nome e complemento, mais um ordinal para andamentos idênticos no mesmo dia). É a
chave de deduplicação do acompanhamento diário do Iustitia. A garantia vale para listas
completas e para consultas com `desde`. Com `max_movimentacoes`, o dia mais antigo pode
vir cortado.

A documentação interativa (OpenAPI) fica em `/docs`.

Resposta (resumida):

```json
{
  "numeroProcesso": "0002221-74.2006.8.07.0001",
  "tribunal": "TJDFT",
  "encontrado": true,
  "processos": [{
    "numeroProcesso": "0002221-74.2006.8.07.0001",
    "tribunal": "TJDFT", "grau": "G1", "sistema": "PJe",
    "dataAjuizamento": "2018-12-12", "emSegredoDeJustica": false,
    "classe": "CUMPRIMENTO DE SENTENÇA",
    "orgaoJulgador": "1ª Vara da Fazenda Pública do DF",
    "assuntos": ["Espécies de Títulos de Crédito"],
    "movimentos": [
      {"nome": "Recebidos os autos", "dataHora": "2026-11-19T17:45:11-03:00",
       "complemento": null, "possuiDocumento": false, "instancia": "G1"}
    ],
    "movimentosCompletos": true,
    "partes": [{"nome": "…", "tipo": "Autor", "polo": "ATIVO", "advogados": [{"nome": "…", "oab": "DF12345"}]}]
  }],
  "consultadoEm": "2026-11-25T01:52:56Z",
  "avisos": []
}
```

Os nomes dos campos seguem o `ProcessoTribunal`/`MovimentoProcesso` do Iustitia. Erros
vêm em ProblemDetails (RFC 7807) com `codigo` e `transitorio`. Veja [docs/erros.md](docs/erros.md).

No e-SAJ a movimentação tem só **data** (a hora fica 00:00 no fuso de Brasília); no PJe
vem data e hora.

## Dados pessoais

- CPF e CNPJ que o PJe devolve no campo `participante` são **descartados** na leitura;
  o acompanhamento processual não precisa deles (minimização, LGPD).
- Os cassetes de teste em `tests/cassettes/` foram **pseudonimizados** por
  `tools/gravar_cassete.py`: nomes de partes, advogados e magistrados viram `PARTE 1`,
  `ADVOGADO 2`…, e CPF, CNPJ e OAB são mascarados.
- O serviço não grava nada em disco. O cache é só em memória.

## Testes

```bash
pytest            # offline: reproduz cassetes gravados dos portais reais
pytest -m live    # contra os portais (rede brasileira); detecta mudança de layout
```

Quando um portal mudar de layout, o teste `live` falha e o de cassete continua passando.
Nesse caso, regrave o cassete e ajuste o parser:

```bash
python tools/gravar_cassete.py 0000248-42.2024.8.26.0319 tests/cassettes/tjsp_cpopg.json --instancias 1
```

## Estrutura

```
juscraper/
  cnj.py            número CNJ: validação (mod 97) e tribunal (mesmo algoritmo do Iustitia)
  models.py         Processo, Movimentacao, Parte, ResultadoConsulta
  errors.py         erros tipados com código estável
  http.py           throttling, retry/backoff, detecção de bloqueio/captcha
  scrapers/esaj.py  e-SAJ (cpopg/cposg, inline e AJAX)
  scrapers/pje.py   PJe (nova consulta pública REST)
  tribunais.py      registro: tribunal → sistema, URL e estado
  servico.py        Juscraper.consultar() + cache
  api.py            FastAPI
  cli.py            linha de comando
integrations/iustitia/   adaptador .NET para o IConsultaProcessoTribunal do Iustitia
tools/gravar_cassete.py  grava e pseudonimiza cassetes de teste
```

## Aviso

Os dados vêm de portais de consulta pública e podem estar incompletos, atrasados ou sob
segredo de justiça. A informação do portal não substitui a intimação oficial nem a
conferência do advogado responsável.
