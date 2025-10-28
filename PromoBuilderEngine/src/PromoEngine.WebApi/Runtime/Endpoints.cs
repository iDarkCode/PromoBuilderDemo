using FastEndpoints; using MediatR; using PromoEngine.Application;
namespace PromoEngine.WebApi.Runtime{
 public sealed class EvaluateEndpoint:Endpoint<EvaluateRequest,IEnumerable<EvaluateResult>>{ private readonly IMediator _med; public EvaluateEndpoint(IMediator med)=>_med=med;
  public override void Configure(){ Post("/api/runtime/evaluate"); AllowAnonymous(); }
  public override async Task HandleAsync(EvaluateRequest req,CancellationToken ct){ var res=await _med.Send(new EvaluatePromotionCommand(req), ct); await SendOkAsync(res, ct);} } }