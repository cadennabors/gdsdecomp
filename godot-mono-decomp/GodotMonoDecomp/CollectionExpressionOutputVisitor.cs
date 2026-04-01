using System.Collections.Generic;
using System.IO;
using System.Linq;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.DebugInfo;
using ICSharpCode.Decompiler.CSharp.OutputVisitor;
using ICSharpCode.Decompiler.CSharp.Syntax;
using ICSharpCode.Decompiler.IL;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.TypeSystem;
using ICSharpCode.Decompiler.Disassembler;
using ICSharpCode.Decompiler.Metadata;
using ICSharpCode.ILSpyX.Extensions;
using SequencePoint = ICSharpCode.Decompiler.DebugInfo.SequencePoint;
using System.Reflection.Metadata;

namespace GodotMonoDecomp;

internal sealed class CollectionExpressionArrayAnnotation
{
	public static readonly CollectionExpressionArrayAnnotation Instance = new();

	private CollectionExpressionArrayAnnotation()
	{
	}
}

internal sealed class CollectionExpressionSpreadElementAnnotation
{
	public static readonly CollectionExpressionSpreadElementAnnotation Instance = new();

	private CollectionExpressionSpreadElementAnnotation()
	{
	}
}
public class ProxyOutput: ITextOutput
{
		public ITextOutput realOutput;
		public ProxyOutput(ITextOutput realOutput)
		{
			this.realOutput = realOutput;
		}
		public string IndentationString {
			get { return realOutput.IndentationString; }
			set { realOutput.IndentationString = value; }
		}
		public void Indent()
		{
			realOutput.Indent();
		}
		public void Unindent()
		{
			realOutput.Unindent();
		}
		public void Write(char ch)
		{
			realOutput.Write(ch);
		}
		public void Write(string text)
		{
			realOutput.Write(text);
		}
		public void WriteLine()
		{
			realOutput.WriteLine();
		}
		public void WriteReference(OpCodeInfo opCode, bool omitSuffix = false)
		{
			realOutput.WriteReference(opCode, omitSuffix);
		}
		public void WriteReference(MetadataFile metadata, Handle handle, string text, string protocol = "decompile", bool isDefinition = false)
		{
			realOutput.WriteReference(metadata, handle, text, protocol, isDefinition);
		}
		public void WriteReference(IType type, string text, bool isDefinition = false)
		{
			realOutput.WriteReference(type, text, isDefinition);
		}
		public void WriteReference(IMember member, string text, bool isDefinition = false)
		{
			realOutput.WriteReference(member, text, isDefinition);
		}
		public void WriteLocalReference(string text, object reference, bool isDefinition = false)
		{
			realOutput.WriteLocalReference(text, reference, isDefinition);
		}

		public void MarkFoldStart(string collapsedText = "...", bool defaultCollapsed = false, bool isDefinition = false)
		{
			realOutput.MarkFoldStart(collapsedText, defaultCollapsed, isDefinition);
		}
		public void MarkFoldEnd()
		{
			realOutput.MarkFoldEnd();
		}
}

public class GodotLineDisassembler: MethodBodyDisassembler
{

	ITextOutput fakeOutput;
	ITextOutput realOutput;

	ProxyOutput proxyOutput;
	IList<SequencePoint> sequencePoints;
	public GodotLineDisassembler(ITextOutput output, CancellationToken cancellationToken, List<SequencePoint> sequencePointsToDecompile) : this(new ProxyOutput(new PlainTextOutput()), output, cancellationToken, sequencePointsToDecompile)
	{
	}

	private GodotLineDisassembler(ProxyOutput proxyOutput, ITextOutput output, CancellationToken cancellationToken, List<SequencePoint> sequencePointsToDecompile) : base(proxyOutput, cancellationToken){
		this.fakeOutput = proxyOutput.realOutput;
		this.proxyOutput = proxyOutput;
		this.sequencePoints = sequencePointsToDecompile;
		this.realOutput = output;
	}

	public override void Disassemble(MetadataFile module, System.Reflection.Metadata.MethodDefinitionHandle handle)
	{
		base.Disassemble(module, handle);
	}

	protected override void WriteInstruction(ITextOutput _, MetadataFile metadataFile, System.Reflection.Metadata.MethodDefinitionHandle methodHandle, ref System.Reflection.Metadata.BlobReader blob, int methodRva)
	{
		int offset = blob.Offset;
		if (sequencePoints.Any(seq => (seq.Offset <= offset && seq.EndOffset > offset)))
		{
			this.proxyOutput.realOutput = realOutput;
			base.WriteInstruction(realOutput, metadataFile, methodHandle, ref blob, methodRva);
		}
		else
		{
			this.proxyOutput.realOutput = fakeOutput;
			base.WriteInstruction(fakeOutput, metadataFile, methodHandle, ref blob, methodRva);
		}
		this.proxyOutput.realOutput = fakeOutput;
	}
}

public class GodotCSharpOutputVisitor : CSharpOutputVisitor
{
	private readonly bool emitILAnnotationComments;
	private readonly TokenWriter commentWriter;

	private readonly GodotMonoDecompSettings settings;

	private Dictionary<ILFunction, List<ICSharpCode.Decompiler.DebugInfo.SequencePoint>> sequencePoints = [];

	private Dictionary<int, List<KeyValuePair<ILFunction, List<ICSharpCode.Decompiler.DebugInfo.SequencePoint>>>> startLineToSequencePoints = [];

	private readonly CSharpDecompiler? decompiler;

	private int lastStartLine = 0;

	public GodotCSharpOutputVisitor(TextWriter w, GodotMonoDecompSettings settings, bool emitILAnnotationComments = false, CSharpDecompiler? decompiler = null)
		: this(new TextWriterTokenWriter(w), settings, emitILAnnotationComments, decompiler)
	{
	}

	private GodotCSharpOutputVisitor(TokenWriter w, GodotMonoDecompSettings settings,
		bool emitILAnnotationComments, CSharpDecompiler? decompiler) : base(w, settings.CSharpFormattingOptions)
	{
		this.decompiler = decompiler;
		this.emitILAnnotationComments = emitILAnnotationComments;
		this.settings = settings;
		this.commentWriter = w;
	}

	// we have to create another visitor and output to a fake writer in order
	// for the output visitor to annotate all the nodes with line/column information
	// So that we can use this to generate sequence points for the IL instructions
	static void WriteCode(TextWriter output, GodotMonoDecompSettings settings, SyntaxTree syntaxTree, IDecompilerTypeSystem typeSystem)
	{
		syntaxTree.AcceptVisitor(new InsertParenthesesVisitor { InsertParenthesesForReadability = true });
		TokenWriter tokenWriter = new TextWriterTokenWriter(output) { IndentationString = settings.CSharpFormattingOptions.IndentationString };
		tokenWriter = TokenWriter.WrapInWriterThatSetsLocationsInAST(tokenWriter);
		syntaxTree.AcceptVisitor(new GodotCSharpOutputVisitor(tokenWriter, settings, false, null));
	}

	public override void VisitSyntaxTree(SyntaxTree syntaxTree)
	{
		if (emitILAnnotationComments && decompiler != null) {
			var fakeWriter = new StringWriter();
			WriteCode(fakeWriter, settings, syntaxTree, decompiler.TypeSystem);
			sequencePoints = decompiler.CreateSequencePoints(syntaxTree);

			// create a
			var startLines = sequencePoints.Where(kvp => kvp.Value.Count > 0).SelectMany(kvp => kvp.Value.Select(seq => seq.StartLine)).Distinct().OrderBy(l => l).ToList();
			foreach (var startLine in startLines)
			{
				var sequencePointsForLine = sequencePoints.Select(kvp => {
					return new KeyValuePair<ILFunction, List<ICSharpCode.Decompiler.DebugInfo.SequencePoint>>(kvp.Key, kvp.Value.Where(seq => seq.StartLine == startLine).ToList());
				}).Where(kvp => kvp.Value.Count > 0).ToList();
				startLineToSequencePoints[startLine] = sequencePointsForLine;
			}
		}
		base.VisitSyntaxTree(syntaxTree);
	}

	protected override void StartNode(AstNode node)
	{
		var startLine = node.StartLocation.Line;
		if (emitILAnnotationComments && startLine > lastStartLine && startLineToSequencePoints.ContainsKey(startLine)) {
			var sequencePointsForLine = startLineToSequencePoints[startLine];
			// transform it into a list of (methodHandle, sequencePoint)
			var sps = sequencePointsForLine
				.SelectMany(kvp => kvp.Value.Select(sp => (Method: kvp.Key, SequencePoint: sp)))
				.OrderBy(sp => sp.SequencePoint.Offset)
				.ToList();
			foreach (var (Method, SequencePoint) in sps) {

				MethodDefinitionHandle? methodHandle = (MethodDefinitionHandle)Method.Method?.MetadataToken;
				if (methodHandle == null) {
					continue;
				}

				var output = new PlainTextOutput();

				var methodDisassembler = new GodotLineDisassembler(output, default(CancellationToken), [SequencePoint]);
				var metadataFile = Method.Method.ParentModule?.MetadataFile;
				if (metadataFile != null) {
					methodDisassembler.Disassemble(metadataFile, methodHandle.Value);
					var text = output.ToString();
					if (text.Length > 0) {
						commentWriter.NewLine();
						foreach (var line in output.ToString().Split('\n')) {
							if (line.Length > 0) {
								commentWriter.WriteComment(CommentType.SingleLine, line);
							}
						}
					}
				}

			}
		}
		lastStartLine = startLine;
		base.StartNode(node);
	}

	public override void VisitArrayInitializerExpression(ArrayInitializerExpression arrayInitializerExpression)
	{
		if (arrayInitializerExpression.Annotation<CollectionExpressionArrayAnnotation>() == null)
		{
			base.VisitArrayInitializerExpression(arrayInitializerExpression);
			return;
		}

		StartNode(arrayInitializerExpression);
		WriteToken(Roles.LBracket);

		bool first = true;
		foreach (var element in arrayInitializerExpression.Elements)
		{
			if (!first)
			{
				WriteToken(Roles.Comma);
				Space();
			}

			if (element.Annotation<CollectionExpressionSpreadElementAnnotation>() != null)
			{
				WriteToken(BinaryOperatorExpression.RangeRole);
			}

			element.AcceptVisitor(this);
			first = false;
		}

		WriteToken(Roles.RBracket);
		EndNode(arrayInitializerExpression);
	}
}
