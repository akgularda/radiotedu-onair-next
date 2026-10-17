using OnAirNext.Installation;
using Xunit;

namespace OnAirNext.Tests;

public sealed class UpdateReviewHandoffTests
{
    private const string Id="0123456789abcdef0123456789abcdef",Sid="S-1-5-21-1-2-3-1001";
    private static readonly string Hash=new('A',64);
    private static readonly string Folder=Path.Combine(Path.GetTempPath(),"original-operator","private-update-reviews");
    private static UpdateReviewRequest Request()=>new(Id,Sid,Hash);
    private static AppUpdateReview Review()=>new(new(Id,new(new('B',64),[]),new(Hash,[]),DateTimeOffset.UtcNow),Sid,Path.GetTempPath(),Hash,Hash,Hash,123,DateTimeOffset.UtcNow,1);
    [Fact] public void Matching_private_original_operator_request_and_receipt_round_trip()
    {
        var request=Request();var path=UpdateReviewHandoff.RequestPath(Folder,Id);
        UpdateReviewHandoff.Validate(request,path,Folder,Sid,Hash.ToLowerInvariant());
        Assert.Equal(Id,UpdateReviewHandoff.IdFromRequestPath(path,Folder));
        var review=Review();Assert.Same(review,UpdateReviewHandoff.ReadResult(request,new(Id,review,null)));
    }
    [Theory] [InlineData("../escape")] [InlineData("")] [InlineData("0123456789abcdef0123456789abcdef/other")]
    public void Invalid_request_identity_cannot_select_a_result_file(string id)=>Assert.Throws<ArgumentException>(()=>UpdateReviewHandoff.ResultPath(Folder,id));
    [Fact] public void Elevated_helper_rejects_an_arbitrary_input_file_before_reading_it()
    {
        var outside=Path.Combine(Path.GetTempPath(),Id+".request.json");
        Assert.Throws<ArgumentException>(()=>UpdateReviewHandoff.IdFromRequestPath(outside,Folder));
        Assert.Throws<ArgumentException>(()=>UpdateReviewHandoff.IdFromRequestPath(Path.Combine(Folder,Id+".result.json"),Folder));
    }
    [Fact] public void Another_administrator_identity_cannot_approve_the_original_operator_request()=>Assert.Throws<InvalidOperationException>(()=>UpdateReviewHandoff.Validate(Request(),UpdateReviewHandoff.RequestPath(Folder,Id),Folder,"S-1-5-21-9-9-9-1001",Hash));
    [Fact] public void Changed_or_malformed_package_identity_rejects_the_handoff()
    {
        var path=UpdateReviewHandoff.RequestPath(Folder,Id);
        Assert.Throws<InvalidOperationException>(()=>UpdateReviewHandoff.Validate(Request(),path,Folder,Sid,new('C',64)));
        Assert.Throws<ArgumentException>(()=>UpdateReviewHandoff.Validate(Request() with{PackageSha256=new('Z',64)},path,Folder,Sid,Hash));
    }
    [Fact] public void Another_result_request_cannot_be_adopted()=>Assert.Throws<IOException>(()=>UpdateReviewHandoff.ReadResult(Request(),new(new('f',32),Review(),null)));
    [Fact] public void Result_cannot_substitute_an_operator_or_package()
    {
        Assert.Throws<IOException>(()=>UpdateReviewHandoff.ReadResult(Request(),new(Id,Review() with{OperatorSid="S-1-5-19"},null)));
        var review=Review();review=review with{Files=review.Files with{Replacement=new(new('D',64),[])}};
        Assert.Throws<IOException>(()=>UpdateReviewHandoff.ReadResult(Request(),new(Id,review,null)));
    }
    [Fact] public void An_explicit_failed_review_is_never_treated_as_completion()=>Assert.Throws<InvalidOperationException>(()=>UpdateReviewHandoff.ReadResult(Request(),new(Id,null,"Windows denied access; no service was changed.")));
}
