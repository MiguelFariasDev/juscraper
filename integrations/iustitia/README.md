# Integração com o Iustitia

Estes arquivos são o adaptador .NET que liga o juscraper à porta
`IConsultaProcessoTribunal` do módulo Legal do Iustitia. Foram escritos seguindo as
convenções do repositório (Result Pattern, `ErrorFactory`, `AddStandardResilienceHandler`,
nunca logar o corpo da resposta), mas **não foram compilados**: o ambiente onde foram
escritos não tinha o SDK .NET. Rode `dotnet build` e os testes ao copiar.

## Por que isso encaixa sem refatoração

O ADR-064 do Iustitia colocou a porta no módulo Legal justamente porque o DataJud "tem
prazo de validade". Assim, trocar a fonte exige só um novo adaptador. O handler
(`ConsultarProcessoTribunalHandler`), o endpoint e a tela continuam iguais.

## Passo a passo

1. Copie os três `.cs` para
   `src/Modules/Legal/Legal.Infrastructure/Integrations/Juscraper/`.
2. Em `Legal.Infrastructure.csproj`, garanta a referência a
   `Microsoft.Extensions.Http.Resilience`. Ela já é usada pelo módulo Integrations.CNJ;
   adicione a versão em `Directory.Packages.props` se o Legal ainda não a referenciar.
3. Em `Legal.Infrastructure/DependencyInjection.cs`, troque

   ```csharp
   services.AddScoped<IConsultaProcessoTribunal, DataJudConsultaProcessoTribunal>();
   ```

   por

   ```csharp
   services.AddJuscraperConsultaProcessoTribunal(configuration);
   ```

4. Configure em `appsettings.json`, com a chave em user-secrets ou Key Vault:

   ```json
   "Juscraper": {
     "BaseUrl": "http://juscraper:8000/",
     "AttemptTimeout": "00:01:00"
   }
   ```

   ```bash
   dotnet user-secrets set "Juscraper:ApiKey" "<uma das JUSCRAPER_API_KEYS>"
   ```

5. Suba o serviço junto (docker-compose do Iustitia):

   ```yaml
   juscraper:
     build: ../juscraper
     environment:
       JUSCRAPER_API_KEYS: ${JUSCRAPER_API_KEY}
     ports: ["8000:8000"]
   ```

## Decisões do adaptador

- **Metadados do 1º grau, movimentos de todas as instâncias.** O cadastro é do processo de
  origem (vara, classe, distribuição). Mas, com recurso pendente, o andamento que importa
  está no tribunal; por isso a timeline mistura G1 e G2, ordenada por data.
- **Complemento no nome do movimento.** No e-SAJ, "Expedido Certidão" sozinho não diz
  nada; o complemento ("Certidão de Remessa da Intimação…") diz. Como `MovimentoProcesso`
  só tem `Nome`, os dois vão juntos, com o complemento truncado em 300 caracteres.
- **`max_movimentacoes=10` por instância.** A tela mostra 10 (o mesmo teto do adaptador
  DataJud). Como os portais devolvem do mais recente para o mais antigo, isso basta, e no
  TJSP a consulta cai de ~12 para 2 requisições ao portal.
- **Mapeamento de erros** para o catálogo existente, sem criar códigos novos:

  | juscraper | Iustitia |
  |---|---|
  | `NUMERO_CNJ_INVALIDO` | `CNJ_INVALID_REQUEST` |
  | `TRIBUNAL_NAO_SUPORTADO`, `CAPTCHA_EXIGIDO`, `ACESSO_BLOQUEADO` | `CNJ_TRIBUNAL_NOT_SUPPORTED` |
  | `TRIBUNAL_INDISPONIVEL` | `CNJ_UNAVAILABLE` |
  | `RESPOSTA_INESPERADA` | `CNJ_INVALID_RESPONSE` |
  | `NAO_AUTORIZADO` / 401 | `CNJ_AUTH_FAILED` |
  | timeout | `CNJ_TIMEOUT` |

  Ajuste sugerido: a mensagem de `CNJ_TRIBUNAL_NOT_SUPPORTED` no `ErrorCatalog` diz
  "…pela consulta pública do **DataJud**". Com o juscraper, ela passa a ser imprecisa;
  algo como "A consulta automática não está disponível para o tribunal '{0}'. Preencha
  os dados manualmente." serve às duas fontes.
- **Uma só nova tentativa** no Polly. O juscraper já faz retry com backoff contra o
  portal; repetir mais no Iustitia multiplicaria as requisições ao tribunal.

## Front-end

O `getTribunalFromCnj` do front-end habilita o botão "consultar no TJxx" com base na
cobertura do DataJud. Com o juscraper, a lista de tribunais suportados muda. Ela pode
vir de `GET /v1/tribunais` (campo `status`) em vez de ficar fixa no código.

## Acompanhamento contínuo (próximo passo)

A porta atual serve ao auto-preenchimento do cadastro, que é uma consulta por clique. Para
**acompanhar** movimentações de processos já cadastrados, o endpoint indicado é

```
GET /v1/processos/{numero}/movimentacoes?desde=2026-11-01
```

chamado por um job do Hangfire (como o `CnjCaptureJob`), espaçado no tempo e de
madrugada, um processo por vez. A deduplicação pode usar (número, instância, data, nome).
Esse job **não** está implementado aqui: ele envolve modelagem de domínio no Iustitia
(entidade de movimentação, alerta, revisão humana do ADR-012).
